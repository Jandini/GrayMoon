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
    /// <param name="onlyLevel">Optional. When set, only repositories at this exact dependency level are processed.</param>
    public async Task RunUpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> setRepositoryError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? onlyLevel = null)
    {
        try
        {
            await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                setProgress,
                setRepositoryError,
                onAppSideComplete,
                repoIdsToUpdate,
                commitMessage,
                includeDepsInCommitMessage,
                onlyLevel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running dependency update for workspace {WorkspaceId}", workspaceId);
            throw;
        }
    }
}

