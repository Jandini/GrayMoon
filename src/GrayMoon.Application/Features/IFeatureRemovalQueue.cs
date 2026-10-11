namespace GrayMoon.Application.Features;

/// <summary>
/// Runs Remove Feature in the background after the user confirms the dialog. Up to
/// <see cref="MaxConcurrentRemovals"/> Features are removed at the same time; the rest wait their turn.
/// </summary>
public interface IFeatureRemovalQueue
{
    const int MaxConcurrentRemovals = 4;

    /// <summary>
    /// Queues the removal. Returns false when this Feature is already queued or being removed.
    /// Throws when the Feature context does not exist or is the special Workspace context.
    /// </summary>
    Task<bool> TryEnqueueAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>True while the Feature is queued or being removed.</summary>
    bool IsPending(WorkspaceFeatureContextId featureContextId);

    /// <summary>Raised on a background thread when a queued removal has finished, successfully or not.</summary>
    event Action<FeatureRemovalCompleted>? Completed;
}

public sealed record FeatureRemovalCompleted(
    int WorkspaceId,
    WorkspaceFeatureContextId FeatureContextId,
    string FeatureName,
    OperationResult Result);
