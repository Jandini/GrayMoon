using System.Collections.Concurrent;
using System.Threading.Channels;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Removes Features in the background once the user has confirmed the Remove Feature dialog. Up to
/// <see cref="IFeatureRemovalQueue.MaxConcurrentRemovals"/> run at the same time; the rest wait in order. The outcome
/// is raised through <see cref="Completed"/>; each open tab shows it as a toast (<c>Toast.razor</c>), because this
/// singleton cannot hold the per-circuit <c>IToastService</c>.
/// </summary>
public sealed class FeatureRemovalQueue(
    IServiceScopeFactory scopeFactory,
    IFeatureFolderCleanupService folderCleanup,
    ILogger<FeatureRemovalQueue> logger,
    TimeProvider? timeProvider = null)
    : BackgroundService, IFeatureRemovalQueue
{
    private static readonly TimeSpan ShutdownMarkTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<QueuedRemoval> _channel = Channel.CreateUnbounded<QueuedRemoval>();
    private readonly ConcurrentDictionary<int, byte> _pending = new();

    public event Action<FeatureRemovalCompleted>? Completed;

    public bool IsPending(WorkspaceFeatureContextId featureContextId) => _pending.ContainsKey(featureContextId.Value);

    public async Task<bool> TryEnqueueAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        CancellationToken cancellationToken = default)
    {
        WorkspaceFeatureContextInfo info;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
            info = await resolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        }

        if (info.IsSpecialWorkspace)
            throw new InvalidOperationException("Cannot remove the special Workspace context.");

        if (!_pending.TryAdd(featureContextId.Value, 0))
            return false;

        var queued = new QueuedRemoval(info.WorkspaceId, featureContextId, info.FeatureName ?? "", options);
        if (!_channel.Writer.TryWrite(queued))
        {
            _pending.TryRemove(featureContextId.Value, out _);
            return false;
        }

        logger.LogInformation(
            "Feature removal queued. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", info.WorkspaceId, info.FeatureName);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The queue lives in memory, so nothing can be mid-removal when the App starts: a Feature still marked
        // Removing was cut short by the previous shutdown or a crash. Release it now instead of waiting for the
        // next Worker connect to run the reconciler.
        await RecoverInterruptedRemovalsAsync(stoppingToken);

        var workers = Enumerable.Range(0, IFeatureRemovalQueue.MaxConcurrentRemovals)
            .Select(_ => Task.Run(() => RunWorkerAsync(stoppingToken), CancellationToken.None));
        await Task.WhenAll(workers);

        // Removals that were queued but never started changed nothing; they are dropped with the process.
        while (_channel.Reader.TryRead(out var dropped))
        {
            _pending.TryRemove(dropped.FeatureContextId.Value, out _);
            logger.LogWarning(
                "Queued Feature removal dropped at shutdown; the Feature is unchanged. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                dropped.WorkspaceId, dropped.FeatureName);
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var removal in _channel.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(removal, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; RunAsync already moved a removal that was cut short out of the Removing state.
        }
    }

    /// <summary>
    /// Marks every Feature still in <c>Removing</c> as <c>NeedsRepair</c> ("Interrupted while removing"), so Remove can
    /// be run again. Called at startup, when no removal can be running. Returns the number of Features released.
    /// Never throws except for cancellation: a database problem here must not stop the queue from starting.
    /// </summary>
    internal async Task<int> RecoverInterruptedRemovalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var removing = await db.WorkspaceFeatures
                .Where(f => f.LifecycleState == WorkspaceFeatureLifecycleState.Removing)
                .ToListAsync(cancellationToken);

            var released = 0;
            foreach (var feature in removing)
            {
                var contextIds = await db.WorkspaceFeatureContexts.AsNoTracking()
                    .Where(c => c.WorkspaceFeatureId == feature.WorkspaceFeatureId)
                    .Select(c => c.WorkspaceFeatureContextId)
                    .ToListAsync(cancellationToken);
                if (contextIds.Any(_pending.ContainsKey))
                    continue;

                MarkInterrupted(feature);
                released++;
                logger.LogWarning(
                    "Feature removal was interrupted by a restart; marked NeedsRepair. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                    feature.WorkspaceId, feature.Name);
            }

            if (released > 0)
                await db.SaveChangesAsync(cancellationToken);
            return released;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not release Features left in the Removing state.");
            return 0;
        }
    }

    /// <summary>
    /// Shutdown cut this removal short. Moves its Feature out of <c>Removing</c> right away, on a token of its own
    /// (the stopping token is already cancelled). Best effort: the startup pass covers a failure here.
    /// </summary>
    private async Task MarkInterruptedAtShutdownAsync(QueuedRemoval removal)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownMarkTimeout);
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(timeout.Token);
            var feature = await db.WorkspaceFeatureContexts
                .Where(c => c.WorkspaceFeatureContextId == removal.FeatureContextId.Value)
                .Select(c => c.WorkspaceFeature)
                .FirstOrDefaultAsync(timeout.Token);
            if (feature is not { LifecycleState: WorkspaceFeatureLifecycleState.Removing })
                return;

            MarkInterrupted(feature);
            await db.SaveChangesAsync(timeout.Token);
            logger.LogWarning(
                "Feature removal interrupted by shutdown; marked NeedsRepair. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                removal.WorkspaceId, removal.FeatureName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not mark the interrupted Feature removal at shutdown. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                removal.WorkspaceId, removal.FeatureName);
        }
    }

    private void MarkInterrupted(WorkspaceFeature feature)
    {
        feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
        feature.LastError = WorkspaceFeatureReconciler.InterruptedRemoving;
        feature.UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    }

    private async Task RunAsync(QueuedRemoval removal, CancellationToken stoppingToken)
    {
        OperationResult result;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
            result = await operations.RemoveFeatureAsync(
                removal.FeatureContextId, removal.Options, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await MarkInterruptedAtShutdownAsync(removal);
            _pending.TryRemove(removal.FeatureContextId.Value, out _);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Feature removal failed. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", removal.WorkspaceId, removal.FeatureName);
            result = OperationResult.Fail(ex.Message);
        }

        _pending.TryRemove(removal.FeatureContextId.Value, out _);

        // A folder the Worker had to leave behind (something still has it open) is retried every 30 minutes until it is gone.
        if (result.Success && !string.IsNullOrEmpty(result.RemoveFeaturePendingDeletionFolder))
            folderCleanup.ArmRetry(removal.WorkspaceId);

        Announce(removal, result);
    }

    private void Announce(QueuedRemoval removal, OperationResult result)
    {
        // Subscribers: the feature list drops the finished Feature, and every open tab shows the outcome toast.
        // One failing subscriber must not keep the others from hearing about it.
        var handlers = Completed;
        if (handlers == null)
            return;

        var completed = new FeatureRemovalCompleted(
            removal.WorkspaceId, removal.FeatureContextId, removal.FeatureName, result);
        foreach (var handler in handlers.GetInvocationList().Cast<Action<FeatureRemovalCompleted>>())
        {
            try
            {
                handler(completed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A Feature removal subscriber failed.");
            }
        }
    }

    /// <summary>The toast text for a finished removal: plain success, success with things to know, or the failure.</summary>
    internal static string BuildToast(string featureName, OperationResult result, out bool isError)
    {
        isError = !result.Success;
        if (!result.Success)
            return result.Error ?? "Remove Feature failed.";

        var report = result.RemoveFeatureReport ?? [];
        var parts = new List<string>();
        var warnings = RemoveFeatureModal.BuildReportWarnings(report);
        if (warnings.Count > 0)
            parts.Add("Feature removed with warnings. " + string.Join(" ", warnings));
        if (!string.IsNullOrEmpty(result.RemoveFeaturePendingDeletionFolder))
            parts.Add((parts.Count == 0 ? "Feature removed. " : "") + RemoveFeatureModal.PendingDeletionText);

        return parts.Count == 0 ? "Feature removed." : string.Join(" ", parts);
    }

    private sealed record QueuedRemoval(
        int WorkspaceId,
        WorkspaceFeatureContextId FeatureContextId,
        string FeatureName,
        RemoveFeatureOptions Options);
}
