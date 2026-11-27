using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Versions;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Create Feature waited for the version stage (GitVersion at every branch point and the registry lookups, about
/// 21 s for 40 repositories) although the Feature was already Ready. The stage now runs after the operation has
/// ended. What the wait guaranteed is kept: the finalization barrier and the hold on the Checkout syncs last until
/// the stage has ended, and an operation that depends on versions waits for it in its start check.
/// </summary>
public sealed class CreateFeatureVersionStageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Create_ends_with_a_Ready_Feature_while_the_version_stage_still_holds_the_barrier_and_the_Checkout_syncs()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = await NewContextAsync(release);

        var result = await CreateAsync(ctx, "feature/usable");

        Assert.True(result.Success, result.Error);
        var contextId = result.ContextId!.Value;
        Assert.Equal(WorkspaceFeatureLifecycleState.Ready, await ReadLifecycleAsync(ctx, contextId));
        var coordinator = ctx.Resolve<FeatureFinalizationCoordinator>();
        // The stage is at the release of the hold, which the Worker has not answered yet.
        await Wait.UntilAsync(() => HoldCalls(ctx).Count(h => !h) == 1);
        Assert.True(coordinator.IsFinalizing(contextId.Value));

        release.SetResult();
        await ctx.Resolve<FeatureVersionStage>().WhenIdleAsync();

        Assert.False(coordinator.IsFinalizing(contextId.Value));
        // Held before the first worktree, released once, after the last one.
        var calls = ctx.WorkerBridge.Calls
            .Where(c => c.Command is WorkerHubMethods.HoldWorktreeHookSyncs or WorkerHubMethods.CreateGitWorktree)
            .Select(c => c.Command == WorkerHubMethods.CreateGitWorktree ? "create" : Arg(c.Args, "hold") is true ? "hold" : "release")
            .ToList();
        Assert.Equal(["hold", "create", "release"], calls);
        Assert.All(
            ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.HoldWorktreeHookSyncs),
            c => Assert.Equal($"create-feature-{contextId.Value}", Arg(c.Args, "key")));
        // Open pages are told once that the versions are there.
        Assert.Contains(ctx.Broadcasts, b => b.Method == "ContextSynced" && Equals(b.Args[1], contextId.Value));
    }

    [Fact]
    public async Task An_operation_that_depends_on_versions_waits_for_the_version_stage_in_its_start_check()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = await NewContextAsync(release);
        var result = await CreateAsync(ctx, "feature/gated");
        var contextId = result.ContextId!.Value;
        await Wait.UntilAsync(() => HoldCalls(ctx).Count(h => !h) == 1);

        // What Update, Update and Push, Prepare Workspace and bulk Push run first.
        await using var scope = ctx.CreateScope();
        var steps = new RecordingProgress();
        var startCheck = scope.ServiceProvider.GetRequiredService<EffectiveVersionStartCheck>()
            .RunAsync(ctx.WorkspaceId, contextId, repositoryIds: null, "Push", CancellationToken.None, steps);
        await Wait.UntilAsync(() => steps.Messages.Contains(EffectiveVersionStartCheck.WaitingForVersionsMessage));
        Assert.False(startCheck.IsCompleted);

        release.SetResult();
        var resolution = await startCheck.WaitAsync(Timeout);

        Assert.Null(resolution.StopMessage);
        Assert.False(ctx.Resolve<FeatureFinalizationCoordinator>().IsFinalizing(contextId.Value));
    }

    [Fact]
    public async Task A_start_check_of_a_context_that_is_not_finalizing_does_not_wait_or_announce_a_wait()
    {
        await using var ctx = await NewContextAsync(release: null);
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var steps = new RecordingProgress();

        await scope.ServiceProvider.GetRequiredService<EffectiveVersionStartCheck>()
            .RunAsync(ctx.WorkspaceId, special, repositoryIds: null, "Push", CancellationToken.None, steps)
            .WaitAsync(Timeout);

        Assert.DoesNotContain(EffectiveVersionStartCheck.WaitingForVersionsMessage, steps.Messages);
    }

    [Fact]
    public async Task A_Create_that_fails_before_the_version_stage_releases_the_hold_itself_and_leaves_no_barrier()
    {
        await using var ctx = await NewContextAsync(release: null, failCreate: true);

        var result = await CreateAsync(ctx, "feature/failed");

        Assert.False(result.Success);
        await ctx.Resolve<FeatureVersionStage>().WhenIdleAsync();
        Assert.Equal([true, false], HoldCalls(ctx));
        Assert.False(ctx.Resolve<FeatureFinalizationCoordinator>().IsFinalizing(result.ContextId!.Value.Value));
    }

    [Fact]
    public async Task A_version_stage_that_cannot_resolve_still_releases_the_hold_and_the_barrier()
    {
        // No resolver in this host: the stage fails at its first step, as it would with a broken database.
        var bridge = new FakeWorkerBridge();
        bridge.Respond(WorkerHubMethods.HoldWorktreeHookSyncs, new { success = true });
        var hub = new FakeHubContext<WorkspaceSyncHub>();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerBridge>(bridge);
        await using var provider = services.BuildServiceProvider();
        var coordinator = new FeatureFinalizationCoordinator();
        using var stage = new FeatureVersionStage(
            provider.GetRequiredService<IServiceScopeFactory>(), coordinator, hub, NullLogger<FeatureVersionStage>.Instance);
        var contextId = new WorkspaceFeatureContextId(41);

        stage.Start(workspaceId: 7, contextId, "feature/broken");
        await stage.WhenIdleAsync().WaitAsync(Timeout);

        Assert.False(coordinator.IsFinalizing(contextId.Value));
        var call = Assert.Single(bridge.Calls);
        Assert.Equal((WorkerHubMethods.HoldWorktreeHookSyncs, "create-feature-41", (object)false), (call.Command, Arg(call.Args, "key"), Arg(call.Args, "hold")));
        Assert.Contains(hub.ClientsImpl.AllProxy.Sent, b => b.Method == "ContextSynced");
    }

    [Fact]
    public void The_barrier_is_armed_before_Start_returns()
    {
        // Between Create and the stage there must be no moment in which a hook sync of the context could be applied.
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerBridge>(new GatedBridge());
        using var provider = services.BuildServiceProvider();
        var coordinator = new FeatureFinalizationCoordinator();
        using var stage = new FeatureVersionStage(
            provider.GetRequiredService<IServiceScopeFactory>(), coordinator, new FakeHubContext<WorkspaceSyncHub>(), NullLogger<FeatureVersionStage>.Instance);

        stage.Start(workspaceId: 7, new WorkspaceFeatureContextId(42), "feature/armed");

        Assert.True(coordinator.IsFinalizing(42));
    }

    /// <summary>Never answers, so the stage it serves stays where the test left it until the stage is disposed.</summary>
    private sealed class GatedBridge : IWorkerBridge
    {
        public bool IsWorkerConnected => true;

        public string? GetUnavailableReason() => null;

        public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
            => new TaskCompletionSource<WorkerCommandResponse>().Task;
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public void Report(OperationProgress value)
        {
            lock (_messages)
                _messages.Add(value.Message);
        }
    }

    private static object? Arg(object args, string name) => args.GetType().GetProperty(name)?.GetValue(args);

    private static List<bool> HoldCalls(SyncStateTestContext ctx)
    {
        // The stage adds to the list from another thread: a snapshot taken during an add is taken again.
        while (true)
        {
            try
            {
                return ctx.WorkerBridge.Calls.ToArray()
                    .Where(c => c.Command == WorkerHubMethods.HoldWorktreeHookSyncs)
                    .Select(c => Arg(c.Args, "hold") is true)
                    .ToList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
            }
        }
    }

    /// <summary>A Workspace of one repository whose host runs the version stage in the background, as production does.</summary>
    /// <param name="release">When given, the Worker answers the release of the hold only once this completes.</param>
    private static async Task<SyncStateTestContext> NewContextAsync(TaskCompletionSource? release, bool failCreate = false)
    {
        var ctx = await SyncStateTestContext.CreateAsync(configureServices: services => services.AddSingleton<FeatureVersionStage>());
        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1" },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["graymoon-api"] = "develop" },
        });
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        if (failCreate)
            ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ => throw new InvalidOperationException("worker gone"));
        else
            ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:wt" });
        ctx.WorkerBridge.RespondAsync(WorkerHubMethods.HoldWorktreeHookSyncs, async (args, _) =>
        {
            if (release is not null && Arg(args, "hold") is false)
                await release.Task;
            return new WorkerCommandResponse(true, new { success = true }, null);
        });
        return ctx;
    }

    private static async Task<CreateFeatureResult> CreateAsync(SyncStateTestContext ctx, string name)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
            .CreateFeatureAsync(ctx.WorkspaceId, name, WorkspaceFeatureBaseKindApplication.CurrentWorkspace)
            .WaitAsync(Timeout);
    }

    private static async Task<WorkspaceFeatureLifecycleState> ReadLifecycleAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceFeatureContexts.AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == contextId.Value)
            .Select(c => c.WorkspaceFeature!.LifecycleState)
            .SingleAsync();
    }
}
