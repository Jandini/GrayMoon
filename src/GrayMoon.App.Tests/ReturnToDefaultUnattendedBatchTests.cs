using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// Bulk unattended return: independent repositories overlap, one blocker does not stop the others,
/// and workspace recompute runs once after the admitted mutations settle.
/// </summary>
public sealed class ReturnToDefaultUnattendedBatchTests
{
    [Fact]
    public async Task Independent_repositories_overlap_and_recompute_once()
    {
        await using var single = await SyncStateTestContext.CreateAsync();
        await PrepareEligibleAsync(single, single.RepositoryId);
        RespondReturn(single);
        var singleHandler = CreateHandler(single);
        await singleHandler.ReturnToDefaultUnattendedBatchAsync(
            single.WorkspaceId, await single.GetSpecialContextIdAsync(), [single.RepositoryId], null, CancellationToken.None);
        var singleBroadcasts = WorkspaceSyncedCount(single);

        await using var ctx = await SyncStateTestContext.CreateAsync();
        var ids = await SeedEligibleAsync(ctx, 3);
        var active = 0;
        var maxActive = 0;
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, RefreshBranchesOk(), null));
        ctx.WorkerBridge.RespondAsync("ReturnToDefaultBranch", async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, now);
            if (Interlocked.Increment(ref entered) == ids.Length)
                allEntered.TrySetResult();
            try
            {
                await release.Task;
                return new WorkerCommandResponse(true, ReturnResponse(), null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var progress = new ListProgress();
        var handler = CreateHandler(ctx);
        var run = handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), ids, progress, CancellationToken.None);

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, WorkspaceSyncedCount(ctx));
        Assert.InRange(Volatile.Read(ref maxActive), 2, 4);
        release.TrySetResult();

        var batch = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ids.Length, batch.Results.Count);
        Assert.All(batch.Results, result => Assert.True(result.Completed && result.MutationCompleted));
        Assert.Equal(ids, batch.Results.Select(result => result.RepositoryId).ToArray());
        Assert.True(batch.ReconciliationAttempted);
        Assert.True(batch.ReconciliationSucceeded);
        Assert.Equal(singleBroadcasts, WorkspaceSyncedCount(ctx));
        Assert.Contains(progress.Messages, message => message.Contains($"of {ids.Length}", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Messages, message => message.Contains("1 of 1", StringComparison.Ordinal));
        Assert.Equal(ids.Length, ctx.WorkerBridge.Calls.Count(call => call.Command == "ReturnToDefaultBranch"));
    }

    [Fact]
    public async Task Setting_one_never_runs_two_returns_at_once()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var ids = await SeedEligibleAsync(ctx, 1);
        var active = 0;
        var maxActive = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, RefreshBranchesOk(), null));
        ctx.WorkerBridge.RespondAsync("ReturnToDefaultBranch", async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, now);
            firstEntered.TrySetResult();
            try
            {
                await release.Task;
                return new WorkerCommandResponse(true, ReturnResponse(), null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var handler = CreateHandler(ctx, maxParallel: 1);
        var run = handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), ids, null, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref maxActive));
        release.TrySetResult();
        var batch = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, batch.Results.Count);
        Assert.All(batch.Results, result => Assert.True(result.MutationCompleted));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Duplicate_repository_ids_run_once_and_keep_first_seen_order()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var second = await AddRepositoryAsync(ctx, "second-repo", ahead: 0, hasUpstream: true);
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        RespondReturn(ctx);

        var handler = CreateHandler(ctx);
        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId,
            await ctx.GetSpecialContextIdAsync(),
            [second, ctx.RepositoryId, second],
            null,
            CancellationToken.None);

        Assert.Equal([second, ctx.RepositoryId], batch.Results.Select(result => result.RepositoryId).ToArray());
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(call => call.Command == "ReturnToDefaultBranch"));
    }

    [Fact]
    public async Task One_blocked_repository_does_not_stop_the_others()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var safe = await AddRepositoryAsync(ctx, "safe-repo", ahead: 0, hasUpstream: true);
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchAheadCommits = 4;
            link.CheckedOutTag = null;
        });
        RespondReturn(ctx);

        var handler = CreateHandler(ctx);
        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId, safe], null, CancellationToken.None);

        var blocked = batch.Results.Single(result => result.RepositoryId == ctx.RepositoryId);
        var returned = batch.Results.Single(result => result.RepositoryId == safe);
        Assert.False(blocked.Completed);
        Assert.False(blocked.MutationAttempted);
        Assert.Equal(UnattendedReturnFailureStage.Eligibility, blocked.FailureStage);
        Assert.Contains("not safe", blocked.AbortReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(returned.Completed);
        Assert.True(returned.MutationCompleted);
        Assert.True(batch.ReconciliationAttempted);
        Assert.DoesNotContain(
            ctx.WorkerBridge.Calls.Where(call => call.Command == "ReturnToDefaultBranch"),
            call => NameOf(call.Args) == "graymoon-api");
        Assert.Contains(
            ctx.WorkerBridge.Calls.Where(call => call.Command == "ReturnToDefaultBranch"),
            call => NameOf(call.Args) == "safe-repo");
    }

    [Fact]
    public async Task Fetch_failure_is_attributed_and_the_other_repository_still_returns()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var other = await AddRepositoryAsync(ctx, "other-repo", ahead: 0, hasUpstream: false);
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("RefreshBranches", args =>
            NameOf(args) == "graymoon-api"
                ? new WorkerCommandResponse(false, null, "fetch failed")
                : new WorkerCommandResponse(true, RefreshBranchesOk(hasUpstream: false), null));
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", ReturnResponse());

        var handler = CreateHandler(ctx);
        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId, other], null, CancellationToken.None);

        var failed = batch.Results.Single(result => result.RepositoryId == ctx.RepositoryId);
        var returned = batch.Results.Single(result => result.RepositoryId == other);
        Assert.Equal(UnattendedReturnFailureStage.Fetch, failed.FailureStage);
        Assert.False(failed.MutationAttempted);
        Assert.True(returned.MutationCompleted);
        var args = ctx.WorkerBridge.Calls.Single(call => call.Command == "ReturnToDefaultBranch").Args;
        Assert.Equal(false, args.GetType().GetProperty("deleteRemoteBranch")!.GetValue(args));
        Assert.Equal(true, args.GetType().GetProperty("forceDeleteLocalBranch")!.GetValue(args));
    }

    [Fact]
    public async Task Result_order_follows_input_when_a_later_repository_finishes_first()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var fast = await AddRepositoryAsync(ctx, "fast-repo", ahead: 0, hasUpstream: true);
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, RefreshBranchesOk(), null));
        ctx.WorkerBridge.RespondAsync("ReturnToDefaultBranch", async (args, _) =>
        {
            if (NameOf(args) == "graymoon-api")
            {
                slowEntered.TrySetResult();
                await releaseSlow.Task;
            }

            return new WorkerCommandResponse(true, ReturnResponse(), null);
        });

        var handler = CreateHandler(ctx);
        var run = handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId, fast], null, CancellationToken.None);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseSlow.TrySetResult();
        var batch = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([ctx.RepositoryId, fast], batch.Results.Select(result => result.RepositoryId).ToArray());
    }

    [Fact]
    public async Task Cancellation_keeps_an_in_flight_success_and_does_not_start_the_next()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var second = await AddRepositoryAsync(ctx, "second-repo", ahead: 0, hasUpstream: true);
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, RefreshBranchesOk(), null));
        ctx.WorkerBridge.RespondAsync("ReturnToDefaultBranch", async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new WorkerCommandResponse(true, ReturnResponse(), null);
        });

        using var cts = new CancellationTokenSource();
        var handler = CreateHandler(ctx, maxParallel: 1);
        var run = handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId, second], null, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        release.TrySetResult();
        var batch = await run.WaitAsync(TimeSpan.FromSeconds(10));

        var first = batch.Results.Single(result => result.RepositoryId == ctx.RepositoryId);
        var waiting = batch.Results.Single(result => result.RepositoryId == second);
        Assert.False(first.Completed);
        Assert.True(first.MutationAttempted);
        Assert.False(first.MutationCompleted);
        Assert.Equal(UnattendedReturnFailureStage.Cancelled, first.FailureStage);
        Assert.Equal(UnattendedReturnFailureStage.Cancelled, waiting.FailureStage);
        Assert.False(waiting.MutationAttempted);
        Assert.False(waiting.MutationCompleted);
        Assert.Equal(1, ctx.WorkerBridge.Calls.Count(call => call.Command == "ReturnToDefaultBranch"));
        Assert.True(batch.ReconciliationAttempted);
        Assert.False(batch.ReconciliationSucceeded);
    }

    [Fact]
    public async Task Feature_context_fails_every_repository_without_mutating()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        RespondReturn(ctx);
        var featureId = await SeedFeatureContextAsync(ctx);

        var handler = CreateHandler(ctx);
        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, featureId, [ctx.RepositoryId], null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(UnattendedReturnFailureStage.Eligibility, result.FailureStage);
        Assert.Contains("Workspace-only", result.AbortReason, StringComparison.Ordinal);
        Assert.False(batch.ReconciliationAttempted);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, call => call.Command == "ReturnToDefaultBranch");
    }

    [Fact]
    public async Task Recompute_failure_does_not_mark_a_finished_return_as_unperformed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddSingleton<IHubContext<WorkspaceSyncHub>>(new ThrowingHubContext());
        });
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        RespondReturn(ctx);

        var handler = CreateHandler(ctx);
        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.True(result.Completed);
        Assert.True(result.MutationCompleted);
        Assert.True(batch.ReconciliationAttempted);
        Assert.False(batch.ReconciliationSucceeded);
        Assert.Equal("Refreshing the workspace failed.", batch.ReconciliationError);
    }

    [Fact]
    public async Task Single_repository_progress_does_not_say_one_of_one()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        RespondReturn(ctx);
        var progress = new ListProgress();
        var handler = CreateHandler(ctx);

        await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], progress, CancellationToken.None);

        Assert.Contains(progress.Messages, message => message.Contains("Returning to default branch", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Messages, message => message.Contains("1 of 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tag_checkout_is_left_untouched()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.CheckedOutTag = "v1.0.0";
            link.BranchName = null;
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
        });
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, new BranchesResponse
        {
            Success = true,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            CurrentBranch = null,
            DefaultBranch = "main",
            Tags = ["v1.0.0"],
            CurrentTag = "v1.0.0",
            UpstreamProbed = false,
        }, null));
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", _ => new WorkerCommandResponse(true, ReturnResponse(), null));
        var progress = new ListProgress();
        var handler = CreateHandler(ctx);

        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], progress, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.True(result.Completed);
        Assert.False(result.MutationAttempted);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, call => call.Command == "ReturnToDefaultBranch");
        Assert.DoesNotContain(progress.Messages, message => message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Batch_progress_omits_failed_counts_when_a_repository_fails()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var ids = await SeedEligibleAsync(ctx, 1);
        ctx.WorkerBridge.Respond("RefreshBranches", _ => new WorkerCommandResponse(true, RefreshBranchesOk(), null));
        var call = 0;
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", _ =>
        {
            call++;
            return call == 1
                ? new WorkerCommandResponse(true, ReturnResponse(), null)
                : new WorkerCommandResponse(false, null, "boom");
        });
        var progress = new ListProgress();
        var handler = CreateHandler(ctx);

        var batch = await handler.ReturnToDefaultUnattendedBatchAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), ids, progress, CancellationToken.None);

        Assert.Contains(batch.Results, result => result.Completed);
        Assert.Contains(batch.Results, result => !result.Completed);
        Assert.DoesNotContain(progress.Messages, message => message.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress.Messages, message => message.Contains("Returned", StringComparison.Ordinal) && message.Contains("of", StringComparison.Ordinal));
    }

    private static void RespondReturn(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond("RefreshBranches", RefreshBranchesOk());
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", ReturnResponse());
    }

    private static async Task PrepareEligibleAsync(SyncStateTestContext ctx, int repositoryId)
    {
        if (repositoryId != ctx.RepositoryId)
            throw new InvalidOperationException("PrepareEligibleAsync only updates the seeded repository.");

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = true;
            link.CheckedOutTag = null;
        });
    }

    private static async Task<int[]> SeedEligibleAsync(SyncStateTestContext ctx, int extraCount)
    {
        await PrepareEligibleAsync(ctx, ctx.RepositoryId);
        var ids = new List<int> { ctx.RepositoryId };
        for (var i = 0; i < extraCount; i++)
            ids.Add(await AddRepositoryAsync(ctx, $"repo-{i + 2}", ahead: 0, hasUpstream: true));
        return ids.ToArray();
    }

    private static async Task<int> AddRepositoryAsync(SyncStateTestContext ctx, string name, int ahead, bool hasUpstream)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Data.AppDbContext>();
        var connectorId = await db.Connectors.Select(connector => connector.ConnectorId).FirstAsync();
        var repo = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = name,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{name}.git",
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repo.RepositoryId,
            BranchName = "feature/x",
            DefaultBranchName = "main",
            DefaultBranchAheadCommits = ahead,
            BranchHasUpstream = hasUpstream,
            CheckedOutTag = null,
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
        return repo.RepositoryId;
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureContextAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Data.AppDbContext>();
        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feature-a",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();
        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static WorkspaceSyncHandler CreateHandler(SyncStateTestContext ctx, int? maxParallel = null)
    {
        var options = maxParallel is int value
            ? Options.Create(new WorkspaceOptions { MaxParallelOperations = value })
            : ctx.Resolve<IOptions<WorkspaceOptions>>();
        return new WorkspaceSyncHandler(NullLogger<WorkspaceSyncHandler>.Instance, ctx.Resolve<IServiceScopeFactory>(), options);
    }

    private static int WorkspaceSyncedCount(SyncStateTestContext ctx)
        => ctx.Broadcasts.Count(broadcast => broadcast.Method == "WorkspaceSynced");

    private static void UpdateMax(ref int maxActive, int now)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref maxActive);
            if (now <= snapshot)
                return;
        }
        while (Interlocked.CompareExchange(ref maxActive, now, snapshot) != snapshot);
    }

    private static string? NameOf(object args)
        => args.GetType().GetProperty("repositoryName")!.GetValue(args) as string;

    private static BranchesResponse RefreshBranchesOk(bool hasUpstream = true) => new()
    {
        Success = true,
        LocalBranches = ["main", "feature/x"],
        RemoteBranches = ["origin/main", "origin/feature/x"],
        CurrentBranch = "feature/x",
        DefaultBranch = "main",
        Tags = [],
        HasUpstream = hasUpstream,
        UpstreamProbed = true,
    };

    private static ReturnToDefaultBranchResponse ReturnResponse() => new()
    {
        Success = true,
        CurrentBranch = "main",
        DefaultBranch = "main",
        LocalBranches = ["main"],
        RemoteBranches = ["origin/main"],
        Tags = [],
        OutgoingCommits = 0,
        IncomingCommits = 0,
        HasUpstream = true,
        DefaultBranchBehind = 0,
        DefaultBranchAhead = 0,
        GitVersion = "2.0.0",
    };

    private sealed class ListProgress : IProgress<OperationProgress>
    {
        public List<string> Messages { get; } = [];

        public void Report(OperationProgress value) => Messages.Add(value.Message);
    }

    private sealed class ThrowingHubContext : IHubContext<WorkspaceSyncHub>
    {
        public IHubClients Clients { get; } = new ThrowingClients();
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class ThrowingClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new ThrowingProxy();
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy OthersInGroup(string groupName) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class ThrowingProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.FromException(new InvalidOperationException("broadcast failed"));
        }
    }
}
