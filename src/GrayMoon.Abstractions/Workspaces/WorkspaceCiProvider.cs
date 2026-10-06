namespace GrayMoon.Abstractions.Workspaces;

/// <summary>
/// Which CI system a workspace is integrated with. An enum rather than a boolean so a future
/// provider needs no schema redesign.
/// </summary>
/// <remarks>
/// GitHub source control and GitHub Actions are separate capabilities: a workspace may use GitHub
/// repositories and pull requests while using another CI provider or none. Nothing here implies
/// anything about <see cref="WorkspaceType"/>.
/// </remarks>
public enum WorkspaceCiProvider
{
    /// <summary>No CI integration. Missing CI configuration is not an error.</summary>
    None = 0,

    /// <summary>GitHub Actions.</summary>
    GitHubActions = 1
}
