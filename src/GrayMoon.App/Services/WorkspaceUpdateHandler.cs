namespace GrayMoon.App.Services;

/// <summary>
/// Handles dependency update operations for a workspace by delegating to DependencyUpdateOrchestrator.
/// Stateless; the caller owns all UI state and file-version handling.
/// </summary>
public sealed class WorkspaceUpdateHandler(
    DependencyUpdateOrchestrator dependencyUpdateOrchestrator,
    ILogger<WorkspaceUpdateHandler> logger)
{
    /// <summary>
    /// Runs the full update flow (refresh, sync deps, commit per level, refresh version, version-file updates) via the orchestrator.
    /// </summary>
    /// <param name="maxLevel">Optional. When set, only repositories at or below this dependency level are processed.</param>
    public async Task<IReadOnlySet<int>> RunUpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> setRepositoryError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null)
    {
        try
        {
            return await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                setProgress,
                setRepositoryError,
                onAppSideComplete,
                repoIdsToUpdate,
                commitMessage,
                includeDepsInCommitMessage,
                maxLevel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running dependency update for workspace {WorkspaceId}", workspaceId);
            throw;
        }
    }
}

