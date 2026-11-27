namespace GrayMoon.Worker.Jobs;

/// <summary>
/// A command response that says whether the command worked. The Worker reads it to fill the hub response, so a
/// failed command reaches the App as failed. Every response type with a <c>Success</c> property implements it
/// (<c>CommandContractConventionTests</c>); nothing reads the property by name, which Native AOT does not support.
/// </summary>
public interface ICommandOutcome
{
    bool Success { get; }

    string? ErrorMessage { get; }
}

/// <summary>A command request that belongs to one Workspace; counted for it in the queue status.</summary>
public interface IWorkspaceScopedRequest
{
    int WorkspaceId { get; }
}

/// <summary>A command request that names its repository; the overlay output is labeled with it.</summary>
public interface IRepositoryNamedRequest
{
    string? RepositoryName { get; }
}

/// <summary>A command request that names its Workspace; the overlay output is labeled with it when no repository is named.</summary>
public interface IWorkspaceNamedRequest
{
    string? WorkspaceName { get; }
}
