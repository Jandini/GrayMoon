using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using GrayMoon.App.Services.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// Several writers persist branch rows at once (push, checkout, hook sync, fetch). Each decides a ref has no
/// row from an earlier read, so another writer can store it first. A first push of a new branch hit this:
/// the hook sync stored <c>origin/&lt;branch&gt;</c> while the push was waiting on the Worker, the push's insert
/// then failed the unique index, and the failure ended a multi-level Push Updated run after one level.
/// Every write path must keep the stored row and finish its own work. The guarantee lives in
/// <see cref="AppDbContext"/> so no writer needs its own handling and a new one is covered by default.
/// </summary>
public sealed class BranchRowSaveRaceTests
{
    private const string Branch = "more-repos";
    private const string RemoteBranch = "origin/more-repos";

    [Fact]
    public async Task A_plain_context_save_keeps_the_row_another_writer_stored_first()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RepositoryBranches.Add(NewRemote(ctx.WorkspaceRepositoryId, RemoteBranch));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, race.Fired);
        Assert.Equal(1, await CountAsync(ctx, RemoteBranch, isRemote: true));
    }

    [Fact]
    public async Task Rows_saved_with_the_lost_row_are_still_written()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RepositoryBranches.Add(NewRemote(ctx.WorkspaceRepositoryId, RemoteBranch));
            db.RepositoryBranches.Add(NewRemote(ctx.WorkspaceRepositoryId, "origin/other"));
            var link = await db.WorkspaceRepositories.SingleAsync(wr => wr.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
            link.OutgoingCommits = 42;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await CountAsync(ctx, RemoteBranch, isRemote: true));
        Assert.Equal(1, await CountAsync(ctx, "origin/other", isRemote: true));
        Assert.Equal(42, (await ctx.ReadLinkAsync()).OutgoingCommits);
    }

    [Fact]
    public async Task A_duplicate_with_no_stored_row_behind_it_still_fails()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepositoryBranches.Add(NewRemote(ctx.WorkspaceRepositoryId, RemoteBranch));
        db.RepositoryBranches.Add(NewRemote(ctx.WorkspaceRepositoryId, RemoteBranch));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Push_completion_survives_the_hook_sync_storing_the_new_remote_branch_first()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());
        var contextId = await ctx.GetSpecialContextIdAsync();
        await SetLinkBranchAsync(ctx, Branch, hasUpstream: false);

        ctx.WorkerBridge.IsWorkerConnected = false;
        ctx.WorkerBridge.Respond("GetCommitCounts", new { outgoingCommits = 0, incomingCommits = 0, hasUpstream = true });
        await using (var scope = ctx.CreateScope())
        {
            var push = CreatePushService(ctx, scope.ServiceProvider);
            await push.UpdateCommitCountsAndUpstreamAfterPushAsync(
                ctx.WorkspaceId,
                contextId,
                [new PushRepoPayload(ctx.RepositoryId, "graymoon-api", null, [])],
                CancellationToken.None);
        }

        Assert.Equal(1, race.Fired);
        Assert.Equal(1, await CountAsync(ctx, RemoteBranch, isRemote: true));
        var link = await ctx.ReadLinkAsync();
        Assert.True(link.BranchHasUpstream);
        Assert.Equal(0, link.OutgoingCommits);
    }

    [Fact]
    public async Task Single_repository_state_write_survives_a_racing_branch_refresh()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            await writer.ApplyAsync(contextId, ctx.WorkspaceId, ctx.RepositoryId, BranchRefresh(), new RepositoryStateWriteOptions { ReconcilePullRequest = false });
        }

        await AssertBranchRefreshStoredAsync(ctx, race);
    }

    [Fact]
    public async Task Batch_state_write_survives_a_racing_branch_refresh()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            await writer.ApplyBatchAsync(
                contextId,
                ctx.WorkspaceId,
                [(ctx.RepositoryId, BranchRefresh())],
                new RepositoryStateWriteOptions { ReconcilePullRequest = false });
        }

        await AssertBranchRefreshStoredAsync(ctx, race);
    }

    [Fact]
    public async Task Batch_state_write_with_projects_survives_a_racing_branch_refresh()
    {
        var race = new StoreBranchRowFirst(RemoteBranch);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());
        await ctx.UseDotNetDependencyProfileAsync();
        var contextId = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            await writer.ApplyBatchAsync(
                contextId,
                ctx.WorkspaceId,
                [(ctx.RepositoryId, BranchRefresh(projectsProbed: true))],
                new RepositoryStateWriteOptions { ReconcilePullRequest = false });
        }

        await AssertBranchRefreshStoredAsync(ctx, race);
    }

    private static RepositoryStateSnapshot BranchRefresh(bool projectsProbed = false) => new()
    {
        BranchesProbed = true,
        ProjectsProbed = projectsProbed,
        Projects = projectsProbed ? [] : null,
        LocalBranches = ["main", Branch],
        RemoteBranches = ["main", Branch],
        DefaultBranchName = "main",
    };

    private static async Task AssertBranchRefreshStoredAsync(SyncStateTestContext ctx, StoreBranchRowFirst race)
    {
        Assert.Equal(1, race.Fired);
        var rows = await ctx.ReadBranchesAsync();
        Assert.Equal(1, rows.Count(b => b.BranchName == RemoteBranch && b.IsRemote));
        Assert.Equal(1, rows.Count(b => b.BranchName == Branch && !b.IsRemote && !b.IsTag));
        Assert.Equal(1, rows.Count(b => b.BranchName == "origin/main" && b.IsRemote));
        Assert.Equal(1, rows.Count(b => b.BranchName == "main" && !b.IsRemote && b.IsDefault));
    }

    private static async Task SetLinkBranchAsync(SyncStateTestContext ctx, string branchName, bool hasUpstream)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.SingleAsync(wr => wr.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        link.BranchName = branchName;
        link.BranchHasUpstream = hasUpstream;
        await db.SaveChangesAsync();
    }

    private static WorkspacePushService CreatePushService(SyncStateTestContext ctx, IServiceProvider sp) => new(
        ctx.WorkerBridge,
        sp.GetRequiredService<WorkspaceService>(),
        sp.GetRequiredService<WorkspaceRepository>(),
        sp.GetRequiredService<WorkspaceDependencyService>(),
        sp.GetRequiredService<WorkspaceProjectRepository>(),
        sp.GetRequiredService<WorkspaceRepositoryStateWriter>(),
        sp.GetRequiredService<WorkspaceStateRecomputeScope>(),
        sp.GetRequiredService<AppDbContext>(),
        sp.GetRequiredService<IOptions<WorkspaceOptions>>(),
        sp.GetRequiredService<IWorkspaceFeatureContextResolver>(),
        sp.GetRequiredService<IWorkspaceContextPathResolver>(),
        sp.GetRequiredService<IWorkspaceCapabilitiesResolver>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<WorkspacePushService>>(),
        ctx.HubContext);

    private static RepositoryBranch NewRemote(int workspaceRepositoryId, string name) => new()
    {
        WorkspaceRepositoryId = workspaceRepositoryId,
        BranchName = name,
        IsRemote = true,
        LastSeenAt = DateTime.UtcNow,
        IsDefault = false,
    };

    private static async Task<int> CountAsync(SyncStateTestContext ctx, string name, bool isRemote)
    {
        var rows = await ctx.ReadBranchesAsync();
        return rows.Count(b => b.BranchName == name && b.IsRemote == isRemote && !b.IsTag);
    }

    /// <summary>
    /// On the first save that adds <paramref name="branchName"/>, commits that row from another context first,
    /// the same order as the hook sync winning the race against the writer under test.
    /// </summary>
    private sealed class StoreBranchRowFirst(string branchName) : SaveChangesInterceptor
    {
        private IDbContextFactory<AppDbContext>? _factory;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Bind(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_factory != null && eventData.Context is AppDbContext db && Volatile.Read(ref _fired) == 0)
            {
                var pending = db.ChangeTracker.Entries<RepositoryBranch>()
                    .Where(entry => entry.State == EntityState.Added && entry.Entity.BranchName == branchName)
                    .Select(entry => entry.Entity)
                    .ToList();
                if (pending.Count > 0 && Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await using var other = await _factory.CreateDbContextAsync(cancellationToken);
                    foreach (var branch in pending)
                    {
                        other.RepositoryBranches.Add(new RepositoryBranch
                        {
                            WorkspaceRepositoryId = branch.WorkspaceRepositoryId,
                            BranchName = branch.BranchName,
                            IsRemote = branch.IsRemote,
                            IsTag = branch.IsTag,
                            LastSeenAt = DateTime.UtcNow,
                            IsDefault = false,
                        });
                    }

                    await other.SaveChangesAsync(cancellationToken);
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
