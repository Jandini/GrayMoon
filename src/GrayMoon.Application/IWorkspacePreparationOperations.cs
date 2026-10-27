using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public interface IWorkspacePreparationOperations
{
    /// <summary>
    /// Creates <paramref name="newBranchName"/> in every targeted repository, then (only when every one succeeded)
    /// updates dependencies, pushes, or both. With both, the shared combined Update and Push runs and its result is
    /// <see cref="PrepareWorkspaceResult.Combined"/> (a normal push that needs approval is continued through
    /// <see cref="IWorkspaceUpdateAndPushOperations"/>). Push alone is handed back as <see cref="PrepareWorkspaceResult.PushPending"/>.
    /// Progress is reported in order on the reporting thread, so pass a synchronous reporter.
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
