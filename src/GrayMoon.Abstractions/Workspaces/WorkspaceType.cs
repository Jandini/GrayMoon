namespace GrayMoon.Abstractions.Workspaces;

/// <summary>
/// What kind of workspace this is. Governs whether .NET project/package discovery and the
/// dependency graph are active at all; it does not govern repository versioning (see
/// <see cref="WorkspaceVersioningMode"/>) or CI integration (see <see cref="WorkspaceCiProvider"/>).
/// </summary>
/// <remarks>
/// <see cref="Basic"/> is the model default so a brand-new database starts clean. Existing databases
/// are migrated to <see cref="DotNetDependency"/> to preserve today's behaviour - see
/// <c>MigrateWorkspaceProfileColumnsAsync</c>.
/// </remarks>
public enum WorkspaceType
{
    /// <summary>Multi-repository Git workspace: branches, Features, pull requests, changes, workspace files.</summary>
    Basic = 0,

    /// <summary>Adds .NET project and NuGet package discovery, dependency levels, dependency-aware update and package-aware push.</summary>
    DotNetDependency = 1
}
