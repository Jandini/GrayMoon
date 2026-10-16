using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// Sequential-operations audit items 6, 7, 10 (branch query) and 11: Feature roll back fans Source rows out
/// (Workspace root last), rejected-push fetches fan out while their DB updates stay sequential, the cleanup
/// sweep covers Workspaces in parallel, and the common-branches query is one grouped read.
/// </summary>
public sealed class ParallelFanOutTests
{
    private const string FeatureName = "feat-fan-out";
    private const int Limit = 2;
    private const string FeatureRoot = @"C:\Users\test\.graymoon\test-ws\features\feat-fan-out";

    [Fact]
    public async Task Rollback_removes_source_rows_in_parallel_within_the_limit_and_the_root_last()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.MutateLinkAsync(link => link.Role = WorkspaceRepositoryRole.Workspace);
        var names = new[] { "src-a", "src-b", "src-c", "src-d" };
        var sourceIds = await AddLinksAsync(ctx, names);
        var rows = new List<(int, string)> { (ctx.WorkspaceRepositoryId, FeatureRoot) };
        for (var i = 0; i < names.Length; i++)
            rows.Add((sourceIds[i], FeatureRoot + @"\" + names[i]));
        var featureContextId = await SeedFeatureAsync(ctx, rows);

        var probe = new FanOutProbe();
        RespondToRollback(ctx, names, probe, failingPath: null);

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Limit, probe.MaxActive);
        Assert.Equal(1, probe.ActiveWhenRootStarted);
        Assert.Equal(names.Length, probe.SourcesFinishedWhenRootStarted);
        Assert.Equal(names.Length + 1, result.Repositories.Count);
    }

    [Fact]
    public async Task Remove_deletes_source_branches_in_parallel_within_the_limit_and_the_root_last()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.MutateLinkAsync(link => link.Role = WorkspaceRepositoryRole.Workspace);
        var names = new[] { "src-a", "src-b", "src-c", "src-d" };
        var sourceIds = await AddLinksAsync(ctx, names);
        var rows = new List<(int, string)> { (ctx.WorkspaceRepositoryId, FeatureRoot) };
        for (var i = 0; i < names.Length; i++)
            rows.Add((sourceIds[i], FeatureRoot + @"\" + names[i]));
        var featureContextId = await SeedFeatureAsync(ctx, rows);

        var probe = new FanOutProbe();
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.RespondAsync("DeleteBranch", async (args, ct) =>
        {
            var name = JsonSerializer.SerializeToElement(args).GetProperty("repositoryName").GetString();
            var isRoot = name == "graymoon-api";
            if (isRoot)
                probe.MarkRootStarted();
            await probe.EnterAsync(ct);
            if (!isRoot)
                probe.MarkSourceFinished();
            return new WorkerCommandResponse(true, new { success = true }, null);
        });

        var plan = new RemoveFeaturePlan
        {
            Success = true,
            Classification = RemoveFeatureClassification.Completed,
            Repositories = rows.Select(row => new RemoveFeatureRepositoryPlan
            {
                WorkspaceRepositoryId = row.Item1,
                WorktreeExists = true,
                LiveStatusEstablished = true,
                FeatureBranchName = FeatureName,
                FeatureBranchExists = true,
                FeatureBranchAheadOfDefault = 0,
            }).ToList()
        };

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions { AnalyzedPlan = plan });

        Assert.True(result.Success, result.Error);
        Assert.Equal(Limit, probe.MaxActive);
        Assert.Equal(1, probe.ActiveWhenRootStarted);
        Assert.Equal(names.Length, probe.SourcesFinishedWhenRootStarted);
        Assert.Equal(names.Length + 1, ctx.WorkerBridge.Calls.Count(c => c.Command == "DeleteBranch"));
    }

    [Fact]
    public async Task Rollback_keeps_the_root_when_any_parallel_source_failed()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.MutateLinkAsync(link => link.Role = WorkspaceRepositoryRole.Workspace);
        var names = new[] { "src-a", "src-b", "src-c" };
        var sourceIds = await AddLinksAsync(ctx, names);
        var rows = new List<(int, string)> { (ctx.WorkspaceRepositoryId, FeatureRoot) };
        for (var i = 0; i < names.Length; i++)
            rows.Add((sourceIds[i], FeatureRoot + @"\" + names[i]));
        var featureContextId = await SeedFeatureAsync(ctx, rows);

        var probe = new FanOutProbe();
        RespondToRollback(ctx, names, probe, failingPath: FeatureRoot + @"\src-b");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
        Assert.Contains("Workspace repository worktree kept", result.Error);
        var removed = ctx.WorkerBridge.Calls
            .Where(c => c.Command == WorkerHubMethods.RemoveGitWorktree)
            .Select(c => JsonSerializer.SerializeToElement(c.Args).GetProperty("worktreePath").GetString())
            .ToList();
        Assert.Equal(names.Length, removed.Count);
        Assert.DoesNotContain(FeatureRoot, removed);
        Assert.Equal(Limit, probe.MaxActive);
    }

    [Fact]
    public async Task Rejected_push_fetches_fan_out_within_the_limit_and_every_repo_still_gets_its_db_update()
    {
        await using var ctx = await CreateContextAsync();
        var linkIds = await AddLinksAsync(ctx, "src-a", "src-b");
        var repoIds = await ReadRepositoryIdsAsync(ctx);
        Assert.Equal(3, repoIds.Count);

        var probe = new FanOutProbe();
        ctx.WorkerBridge.RespondAsync("RefreshBranches", async (_, ct) =>
        {
            await probe.EnterAsync(ct);
            return new WorkerCommandResponse(true, new { success = true }, null);
        });
        ctx.WorkerBridge.Respond("GetCommitCounts", new { success = true, outgoingCommits = 1, incomingCommits = 0, hasUpstream = true });

        var special = await ctx.GetSpecialContextIdAsync();
        await using (var scope = ctx.CreateScope())
        {
            var push = scope.ServiceProvider.GetRequiredService<WorkspacePushService>();
            await push.FetchAfterRejectionsAsync(
                ctx.WorkspaceId, special, repoIds, "test-ws", @"C:\gm-test-root", null, CancellationToken.None);
        }

        Assert.Equal(Limit, probe.MaxActive);
        Assert.Equal(3, ctx.WorkerBridge.Calls.Count(c => c.Command == "RefreshBranches"));
        Assert.Equal(3, ctx.WorkerBridge.Calls.Count(c => c.Command == "GetCommitCounts"));

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var remotes = await db.RepositoryBranches.AsNoTracking().Where(rb => rb.IsRemote).ToListAsync();
        foreach (var id in new[] { ctx.WorkspaceRepositoryId }.Concat(linkIds))
            Assert.Contains(remotes, rb => rb.WorkspaceRepositoryId == id);
    }

    [Fact]
    public async Task Cleanup_sweep_covers_every_workspace_in_parallel_within_the_limit()
    {
        await using var ctx = await CreateContextAsync();
        await using (var seed = ctx.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 4; i++)
            {
                db.Workspaces.Add(new Workspace
                {
                    Name = $"sweep-ws-{i}",
                    RootPath = @"C:\gm-test-root",
                    ManagedFeatureStorageRoot = $@"C:\Users\test\.graymoon\sweep-ws-{i}\features",
                });
            }

            await db.SaveChangesAsync();
        }

        var probe = new FanOutProbe();
        ctx.WorkerBridge.RespondAsync(WorkerHubMethods.SweepPendingFeatureFolders, async (_, ct) =>
        {
            await probe.EnterAsync(ct);
            return new WorkerCommandResponse(true, new { removed = 0, stillPending = 0, refused = 0 }, null);
        });

        await using var scope = ctx.CreateScope();
        var service = new FeatureFolderCleanupService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Worker.WorkerConnectionTracker>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceOperationLock>(),
            Options.Create(new WorkspaceOptions { MaxParallelOperations = Limit }),
            NullLogger<FeatureFolderCleanupService>.Instance);

        await service.SweepAllAsync();

        Assert.Equal(4, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.SweepPendingFeatureFolders));
        Assert.Equal(Limit, probe.MaxActive);
    }

    [Fact]
    public async Task Common_branches_intersect_across_several_links_from_one_grouped_query()
    {
        await using var ctx = await CreateContextAsync();
        var linkIds = await AddLinksAsync(ctx, "src-a", "src-b");
        var all = new[] { ctx.WorkspaceRepositoryId }.Concat(linkIds).ToList();

        await using (var seed = ctx.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            void Add(int link, string name, bool remote = false, bool isDefault = false, bool tag = false) =>
                db.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = link,
                    BranchName = name,
                    IsRemote = remote,
                    IsDefault = isDefault,
                    IsTag = tag,
                });

            Add(all[0], "main", isDefault: true);
            Add(all[0], "shared");
            Add(all[0], "dev");
            Add(all[0], "only-a");
            Add(all[0], "v1", tag: true);
            Add(all[0], "origin/main", remote: true);
            Add(all[0], "origin/shared", remote: true);
            Add(all[1], "main", isDefault: true);
            Add(all[1], "shared");
            Add(all[1], "dev");
            Add(all[1], "v1", tag: true);
            Add(all[1], "origin/main", remote: true);
            Add(all[1], "origin/shared", remote: true);
            Add(all[1], "origin/only-b", remote: true);
            Add(all[2], "MAIN", isDefault: true);
            Add(all[2], "Shared");
            Add(all[2], "dev");
            Add(all[2], "origin/main", remote: true);
            Add(all[2], "origin/shared", remote: true);
            await db.SaveChangesAsync();
        }

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
        var outcome = await ops.GetCommonBranchesAsync(ctx.WorkspaceId);

        var body = Assert.IsType<CommonBranchesApiResult>(outcome.Body);
        Assert.Equal(["dev", "shared"], body.CommonLocalBranchNames);
        Assert.Equal(["dev", "shared"], body.CommonBranchNames);
        Assert.Equal(["origin/main", "origin/shared"], body.CommonRemoteBranchNames);
        Assert.Equal("main", body.DefaultDisplayText);
    }

    private static Task<SyncStateTestContext> CreateContextAsync()
        => SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = Limit);
            services.AddScoped<WorkspacePushService>();
        });

    private static void RespondToRollback(SyncStateTestContext ctx, string[] sourceNames, FanOutProbe probe, string? failingPath)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            branch = FeatureName,
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.ListGitWorktrees, args =>
        {
            var main = JsonSerializer.SerializeToElement(args).GetProperty("mainRepositoryPath").GetString() ?? "";
            var worktrees = new List<object>
            {
                new { worktreePath = main, branchName = "main", isBare = false },
                new { worktreePath = FeatureRoot, branchName = FeatureName, isBare = false },
            };
            worktrees.AddRange(sourceNames.Select(n =>
                (object)new { worktreePath = FeatureRoot + @"\" + n, branchName = FeatureName, isBare = false }));
            return new WorkerCommandResponse(true, new { worktrees }, null);
        });
        ctx.WorkerBridge.RespondAsync(WorkerHubMethods.RemoveGitWorktree, async (args, ct) =>
        {
            var path = JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString() ?? "";
            var isRoot = path == FeatureRoot;
            if (isRoot)
                probe.MarkRootStarted();
            await probe.EnterAsync(ct);
            if (!isRoot)
                probe.MarkSourceFinished();
            return path == failingPath
                ? new WorkerCommandResponse(false, null, "boom")
                : new WorkerCommandResponse(true, new { success = true }, null);
        });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
    }

    private static async Task<List<int>> AddLinksAsync(SyncStateTestContext ctx, params string[] repositoryNames)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Repositories
            .Where(r => r.RepositoryId == ctx.RepositoryId)
            .Select(r => r.ConnectorId)
            .SingleAsync();

        var ids = new List<int>();
        foreach (var repositoryName in repositoryNames)
        {
            var repo = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = repositoryName,
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{repositoryName}.git",
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();

            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                GitVersion = "1.0.0",
                BranchName = "main",
                DefaultBranchName = "main",
                BranchHasUpstream = true,
                SyncStatus = RepoSyncStatus.InSync,
                RepositoryType = ProjectType.Library,
                Role = WorkspaceRepositoryRole.Source,
            };
            db.WorkspaceRepositories.Add(link);
            await db.SaveChangesAsync();
            ids.Add(link.WorkspaceRepositoryId);
        }

        return ids;
    }

    private static async Task<List<(int RepoId, string RepoName)>> ReadRepositoryIdsAsync(SyncStateTestContext ctx)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => l.WorkspaceId == ctx.WorkspaceId)
            .ToListAsync();
        return links.Select(l => (l.RepositoryId, l.Repository!.RepositoryName)).ToList();
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(SyncStateTestContext ctx, IEnumerable<(int WorkspaceRepositoryId, string WorktreePath)> rows)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        foreach (var row in rows)
        {
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = row.WorkspaceRepositoryId,
                WorktreePath = row.WorktreePath,
                State = WorkspaceFeatureRepositoryState.Ready,
                BaseCommitSha = "abc123",
                ParentBranchName = "main",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    /// <summary>Counts how many fake Worker calls are inside their delay at once.</summary>
    private sealed class FanOutProbe
    {
        private int _active;
        private int _maxActive;
        private int _sourcesFinished;
        private int _activeWhenRootStarted;
        private int _sourcesFinishedWhenRootStarted;

        public int MaxActive => Volatile.Read(ref _maxActive);
        public int ActiveWhenRootStarted => Volatile.Read(ref _activeWhenRootStarted);
        public int SourcesFinishedWhenRootStarted => Volatile.Read(ref _sourcesFinishedWhenRootStarted);

        public void MarkRootStarted()
        {
            // Called before the root call counts itself as active, so every source call must already be done.
            Volatile.Write(ref _sourcesFinishedWhenRootStarted, Volatile.Read(ref _sourcesFinished));
            Volatile.Write(ref _activeWhenRootStarted, Volatile.Read(ref _active) + 1);
        }

        public void MarkSourceFinished() => Interlocked.Increment(ref _sourcesFinished);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)))
            {
                if (Interlocked.CompareExchange(ref _maxActive, now, seen) == seen)
                    break;
            }

            try
            {
                await Task.Delay(60, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
