using System.Collections.Concurrent;
using System.Threading.Channels;
using GrayMoon.App.Components.Features;
using GrayMoon.Application;
using GrayMoon.Application.Features;

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
    ILogger<FeatureRemovalQueue> logger)
    : BackgroundService, IFeatureRemovalQueue
{
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, IFeatureRemovalQueue.MaxConcurrentRemovals)
            .Select(_ => Task.Run(() => RunWorkerAsync(stoppingToken), CancellationToken.None));
        return Task.WhenAll(workers);
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
            // Shutting down; a removal cut short stays in the Removing state and can be continued later.
        }
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
