using System.Diagnostics;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// The version stage of Create Feature, run after the operation has ended. A Feature is usable when its worktrees
/// exist and its projections are seeded; which of its repositories may reuse the published version of their parent
/// branch is a calculation (GitVersion at each branch point, one registry lookup per required package) that the
/// user does not have to watch.
/// </summary>
/// <remarks>
/// <para>
/// What Create guaranteed by waiting is kept by three things that already exist:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The stage holds the Feature's finalization barrier (<see cref="FeatureFinalizationCoordinator.Arm"/>) until it
/// has ended. Hook syncs of that context are set aside by the sync queue meanwhile, so no recompute resolves the
/// same versions beside it.
/// </description></item>
/// <item><description>
/// The Worker's hold on the Checkout syncs of the new worktrees is released by the stage, not by Create, so their
/// GitVersion runs still start after the version stage (<see cref="HoldKey"/>).
/// </description></item>
/// <item><description>
/// Update, Update and Push, Prepare Workspace and bulk Push run <c>EffectiveVersionStartCheck</c>, which waits for
/// the barrier and then decides with fresh registry answers. Nothing is pushed or rewritten from a version that is
/// still being calculated.
/// </description></item>
/// </list>
/// <para>
/// The stage never fails anything: a Worker or registry that cannot answer is logged and the next recompute or
/// operation start resolves the versions. It ends after <see cref="MaxDuration"/> at the latest. When it has ended
/// it announces the context once, so open pages show the versions.
/// </para>
/// </remarks>
public sealed class FeatureVersionStage(
    IServiceScopeFactory scopeFactory,
    FeatureFinalizationCoordinator finalizationCoordinator,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<FeatureVersionStage> logger) : IDisposable
{
    /// <summary>The longest the stage may hold the barrier and the Checkout syncs; the Worker ends its hold after ten minutes anyway.</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly List<Task> _running = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The key of a Feature's hold on the Worker's deferred Checkout syncs.</summary>
    internal static string HoldKey(WorkspaceFeatureContextId contextId) => $"create-feature-{contextId.Value}";

    /// <summary>
    /// Starts the stage for a Feature that is seeded and Ready, and returns at once. The barrier is taken before this
    /// returns, so there is no moment between Create and the stage in which a hook sync of the context could run.
    /// </summary>
    public void Start(int workspaceId, WorkspaceFeatureContextId contextId, string featureName)
    {
        var barrier = finalizationCoordinator.Arm(contextId.Value);
        Task stage;
        lock (_gate)
        {
            stage = Task.Run(() => RunAsync(workspaceId, contextId, featureName, barrier));
            _running.Add(stage);
        }

        _ = stage.ContinueWith(
            t =>
            {
                lock (_gate)
                    _running.Remove(t);
            },
            TaskScheduler.Default);
    }

    /// <summary>Completes when no stage is running. Used by tests.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
                snapshot = [.. _running];
            if (snapshot.Length == 0)
                return;

            await Task.WhenAll(snapshot);
            await Task.Yield();
        }
    }

    private async Task RunAsync(int workspaceId, WorkspaceFeatureContextId contextId, string featureName, IDisposable barrier)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "Resolved";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(MaxDuration);
            await using var scope = scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Versions.EffectiveVersionResolver>();
            await resolver.ResolveAsync(workspaceId, contextId, repositoryIds: null, bypassCache: false, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = _stopping.IsCancellationRequested ? "Stopped" : "TimedOut";
            logger.LogWarning(
                "Effective versions were not resolved after Feature Create ({Outcome}); the next recompute or operation start resolves them. WorkspaceId={WorkspaceId} ContextId={ContextId} FeatureName={FeatureName}",
                outcome, workspaceId, contextId.Value, featureName);
        }
        catch (Exception ex)
        {
            outcome = "Failed";
            logger.LogWarning(ex,
                "Effective versions were not resolved after Feature Create; the next recompute or operation start resolves them. WorkspaceId={WorkspaceId} ContextId={ContextId} FeatureName={FeatureName}",
                workspaceId, contextId.Value, featureName);
        }
        finally
        {
            // In this order: the Checkout syncs may run, then their notifications (and any other that waited) may be applied.
            await ReleaseHoldAsync(contextId);
            barrier.Dispose();
            logger.LogInformation(
                "Feature Create version stage finished. WorkspaceId={WorkspaceId} ContextId={ContextId} FeatureName={FeatureName} Outcome={Outcome} DurationMs={DurationMs}",
                workspaceId, contextId.Value, featureName, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        try
        {
            await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ContextSynced after the version stage was not sent. ContextId={ContextId}", contextId.Value);
        }
    }

    /// <summary>Never throws: the hold only decides when the syncs run, and the Worker ends it by itself after ten minutes.</summary>
    private async Task ReleaseHoldAsync(WorkspaceFeatureContextId contextId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var response = await scope.ServiceProvider.GetRequiredService<IWorkerBridge>().SendCommandAsync(
                WorkerHubMethods.HoldWorktreeHookSyncs,
                new { key = HoldKey(contextId), hold = false },
                CancellationToken.None);
            if (!response.Success)
                logger.LogWarning("Worker did not release the hold on Checkout syncs for context {ContextId}: {Error}", contextId.Value, response.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release the hold on Checkout syncs for context {ContextId}.", contextId.Value);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
