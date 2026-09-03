namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Routes an operation failure to a repository callout or a level callout and logs the actual message.
/// Callers must use <see cref="Repository"/> when a repository id is known and <see cref="Level"/> otherwise.
/// </summary>
public sealed class OperationErrorSink(
    int workspaceId,
    ILogger logger,
    Action<int, string> onRepositoryError,
    Action<int, string> onLevelError)
{
    public void Repository(int repositoryId, string message)
    {
        logger.LogError(
            "Workspace {WorkspaceId}: repository {RepositoryId} error: {Message}",
            workspaceId,
            repositoryId,
            message);
        onRepositoryError(repositoryId, message);
    }

    public void Repository(int repositoryId, Exception exception)
    {
        logger.LogError(
            exception,
            "Workspace {WorkspaceId}: repository {RepositoryId} error: {Message}",
            workspaceId,
            repositoryId,
            exception.Message);
        onRepositoryError(repositoryId, exception.Message);
    }

    public void Level(int level, string message)
    {
        logger.LogError(
            "Workspace {WorkspaceId}: level {Level} error: {Message}",
            workspaceId,
            level,
            message);
        onLevelError(level, message);
    }

    public void Level(int level, Exception exception)
    {
        logger.LogError(
            exception,
            "Workspace {WorkspaceId}: level {Level} error: {Message}",
            workspaceId,
            level,
            exception.Message);
        onLevelError(level, exception.Message);
    }
}
