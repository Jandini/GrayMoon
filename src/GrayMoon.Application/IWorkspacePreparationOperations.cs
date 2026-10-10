using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public interface IWorkspacePreparationOperations
{
    /// <summary>
    /// Creates <paramref name="newBranchName"/> in every targeted repository, then (only when every one succeeded)
    /// updates dependencies, pushes, or both. With both, the update and the push run as the two-lane pipeline; when
    /// that is not possible the update runs here and <see cref="PrepareWorkspaceResult.PushPending"/> hands the push
    /// back to the caller. Progress is reported in order on the reporting thread, so pass a synchronous reporter.
    /// </summary>
    Task<PrepareWorkspaceResult> PrepareAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        bool pushChanges,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken);
}
