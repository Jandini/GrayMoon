using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// The checkbox defaults that follow a type choice in the workspace modal. The user may then override
/// either axis; the three persisted values stay independent.
/// </summary>
public static class WorkspaceProfileDefaults
{
    public static (WorkspaceVersioningMode Versioning, WorkspaceCiProvider Ci) ForType(WorkspaceType type) =>
        type == WorkspaceType.DotNetDependency
            ? (WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.GitHubActions)
            : (WorkspaceVersioningMode.None, WorkspaceCiProvider.None);
}
