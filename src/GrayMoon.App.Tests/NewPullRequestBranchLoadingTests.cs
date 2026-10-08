using System.Data.Common;
using System.Diagnostics;
using GrayMoon.App.Components.Modals;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// New Pull Request first paint: target branches come from one bulk read of persisted branch state - no Worker call, no
/// remote fetch, no persistence, no WorkspaceSynced broadcast - and the number of SQL queries does not grow with the number
/// of repositories (guards against N+1 regressions). Also covers the pure selection rules used on open and after a refresh.
/// </summary>
public sealed class NewPullRequestBranchLoadingTests
{
    // ---- Selection rules --------------------------------------------------------------------------

    private static NewPrTargetRepo Target(string head = "feature/foo", string defaultBranch = "main", string? parent = null) =>
        new(1, "acme", "api", head, defaultBranch, parent, "https://github.com/acme/api.git");

    private static WorkspaceBranchesSnapshot Snapshot(string? defaultBranch, params string[] remotes) => new()
    {
        RemoteBranches = remotes.ToList(),
        DefaultBranch = defaultBranch,
    };

    [Fact]
    public void Resolve_prefers_the_Feature_parent_when_it_still_exists()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(parent: "develop"), Snapshot("main", "origin/main", "origin/develop"));

        Assert.Equal("develop", r.SelectedBase);
        Assert.False(r.NeedsRefresh);
    }

    [Fact]
    public void Resolve_uses_the_default_branch_when_the_parent_is_gone()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(parent: "old"), Snapshot("main", "origin/main", "origin/release"));

        Assert.Equal("main", r.SelectedBase);
    }

    [Fact]
    public void Resolve_never_selects_the_head_branch_as_base()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(head: "main", defaultBranch: "main"), Snapshot("main", "origin/main", "origin/develop"));

        Assert.Equal("develop", r.SelectedBase);
    }

    [Fact]
    public void Resolve_reports_a_repository_without_any_valid_base()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(), snapshot: null);

        Assert.Empty(r.Candidates);
        Assert.True(r.NeedsRefresh);
    }

    [Fact]
    public void Resolve_with_no_persisted_remote_branches_offers_the_default_and_asks_for_a_refresh()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(), Snapshot("main"));

        Assert.Equal(["main"], r.Candidates);
        Assert.Equal("main", r.SelectedBase);
        Assert.True(r.NeedsRefresh);
    }

    [Fact]
    public void Resolve_keeps_a_still_valid_user_selection_after_a_refresh()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(), Snapshot("main", "origin/main", "origin/release/3.0"), currentSelection: "release/3.0");

        Assert.Equal("release/3.0", r.SelectedBase);
    }

    [Fact]
    public void Resolve_drops_a_selection_that_no_longer_exists_after_a_refresh()
    {
        var r = NewPullRequestTargetBranch.Resolve(Target(), Snapshot("main", "origin/main"), currentSelection: "release/3.0");

        Assert.Equal("main", r.SelectedBase);
    }

    // ---- Bulk persisted read ----------------------------------------------------------------------

    [Fact]
    public async Task Bulk_read_returns_each_repositorys_persisted_branches()
    {
        var counter = new QueryCounter();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(counter));
        var repositoryIds = await SeedRepositoriesAsync(ctx, 3);
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
        var snapshots = await ops.GetBranchesForRepositoriesAsync(ctx.WorkspaceId, contextId, repositoryIds);

        Assert.Equal(3, snapshots.Count);
        for (var i = 0; i < repositoryIds.Count; i++)
        {
            var snapshot = snapshots[repositoryIds[i]];
            Assert.Equal("main", snapshot.DefaultBranch);
            Assert.Equal(["origin/main", $"origin/release/{i}"], snapshot.RemoteBranches);
            Assert.Equal(["main", $"release/{i}"], NewPullRequestTargetBranch.BuildBaseCandidates(snapshot));
        }
    }

    [Fact]
    public async Task Bulk_read_matches_the_single_repository_read()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var repositoryIds = await SeedRepositoriesAsync(ctx, 2);
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
        var bulk = await ops.GetBranchesForRepositoriesAsync(ctx.WorkspaceId, contextId, repositoryIds);

        foreach (var id in repositoryIds)
        {
            var single = (WorkspaceBranchesSnapshot)(await ops.GetBranchesAsync(ctx.WorkspaceId, contextId, id)).Body!;
            Assert.Equal(single.RemoteBranches, bulk[id].RemoteBranches);
            Assert.Equal(single.LocalBranches, bulk[id].LocalBranches);
            Assert.Equal(single.Tags, bulk[id].Tags);
            Assert.Equal(single.DefaultBranch, bulk[id].DefaultBranch);
            Assert.Equal(single.CurrentBranch, bulk[id].CurrentBranch);
        }
    }

    [Fact]
    public async Task Bulk_read_skips_repositories_outside_the_workspace()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var repositoryIds = await SeedRepositoriesAsync(ctx, 1);
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
        var snapshots = await ops.GetBranchesForRepositoriesAsync(ctx.WorkspaceId, contextId, [repositoryIds[0], 999_999]);

        Assert.True(snapshots.ContainsKey(repositoryIds[0]));
        Assert.False(snapshots.ContainsKey(999_999));
    }

    [Fact]
    public async Task Bulk_read_for_100_repositories_uses_no_Worker_no_broadcast_and_a_bounded_number_of_queries()
    {
        var counter = new QueryCounter();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(counter));
        var few = await SeedRepositoriesAsync(ctx, 3, namePrefix: "few");
        var many = await SeedRepositoriesAsync(ctx, 100, namePrefix: "many");
        var contextId = await ctx.GetSpecialContextIdAsync();
        var sentBefore = ctx.HubContext.ClientsImpl.AllProxy.Sent.Count;

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        counter.Reset();
        await ops.GetBranchesForRepositoriesAsync(ctx.WorkspaceId, contextId, few);
        var fewQueries = counter.Count;

        counter.Reset();
        var snapshots = await ops.GetBranchesForRepositoriesAsync(ctx.WorkspaceId, contextId, many);
        var manyQueries = counter.Count;

        Assert.Equal(100, snapshots.Count);
        Assert.Equal(fewQueries, manyQueries);
        Assert.True(manyQueries <= 8, $"Expected a fixed handful of queries, got {manyQueries}.");
        Assert.Empty(ctx.WorkerBridge.Calls);
        Assert.Equal(sentBefore, ctx.HubContext.ClientsImpl.AllProxy.Sent.Count);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<List<int>> SeedRepositoriesAsync(SyncStateTestContext ctx, int count, string namePrefix = "repo")
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();

        var repositories = Enumerable.Range(0, count)
            .Select(i => new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = $"{namePrefix}-{i}",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{namePrefix}-{i}.git",
            })
            .ToList();
        db.Repositories.AddRange(repositories);
        await db.SaveChangesAsync();

        var links = repositories
            .Select(r => new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = r.RepositoryId,
                BranchName = "feature/foo",
                DefaultBranchName = "main",
                SyncStatus = RepoSyncStatus.InSync,
            })
            .ToList();
        db.WorkspaceRepositories.AddRange(links);
        await db.SaveChangesAsync();

        for (var i = 0; i < links.Count; i++)
        {
            var linkId = links[i].WorkspaceRepositoryId;
            db.RepositoryBranches.AddRange(
                new RepositoryBranch { WorkspaceRepositoryId = linkId, BranchName = "origin/main", IsRemote = true },
                new RepositoryBranch { WorkspaceRepositoryId = linkId, BranchName = "main", IsRemote = false, IsDefault = true },
                new RepositoryBranch { WorkspaceRepositoryId = linkId, BranchName = $"origin/release/{i}", IsRemote = true },
                new RepositoryBranch { WorkspaceRepositoryId = linkId, BranchName = "feature/foo", IsRemote = false },
                new RepositoryBranch { WorkspaceRepositoryId = linkId, BranchName = "v1.0.0", IsTag = true });
        }

        await db.SaveChangesAsync();
        return repositories.Select(r => r.RepositoryId).ToList();
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }
    }
}
