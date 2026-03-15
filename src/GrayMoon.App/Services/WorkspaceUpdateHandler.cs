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
    /// Runs the full update flow (refresh, sync deps, commit per level, refresh version) via the orchestrator.
    /// </summary>
    public async Task RunUpdateAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> setRepositoryError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null)
    {
        try
        {
            await dependencyUpdateOrchestrator.RunAsync(
                workspaceId,
                cancellationToken,
                setProgress,
                setRepositoryError,
                onAppSideComplete,
                repoIdsToUpdate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running dependency update for workspace {WorkspaceId}", workspaceId);
            throw;
        }
    }
}

