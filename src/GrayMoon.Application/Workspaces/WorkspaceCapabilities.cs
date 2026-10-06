using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Application.Workspaces;

/// <summary>
/// What a workspace can do, derived from the three persisted settings. This is the single place that
/// turns workspace configuration into behaviour decisions.
/// </summary>
/// <remarks>
/// <para>
/// Capabilities are derived, never persisted. Only <see cref="Type"/>,
/// <see cref="VersioningMode"/> and <see cref="CiProvider"/> are stored. If workspace-type checks
/// start accumulating in unrelated files, that is the signal to add a strategy seam rather than
/// another branch.
/// </para>
/// <para>
/// Capability decisions belong at orchestration and UI boundaries. Low-level Git code must not know
/// what a Basic workspace is.
/// </para>
/// </remarks>
public sealed record WorkspaceCapabilities(
    WorkspaceType Type,
    WorkspaceVersioningMode VersioningMode,
    WorkspaceCiProvider CiProvider)
{
    /// <summary>Repository versions are calculated and displayed at all.</summary>
    public bool UsesRepositoryVersioning => VersioningMode != WorkspaceVersioningMode.None;

    /// <summary>GitVersion specifically is the version provider.</summary>
    public bool UsesGitVersion => VersioningMode == WorkspaceVersioningMode.GitVersion;

    /// <summary>The working tree is scanned for .csproj files and package references.</summary>
    public bool DiscoversDotNetProjects => Type == WorkspaceType.DotNetDependency;

    /// <summary>Dependency edges, levels and unmatched-dependency state are produced and read.</summary>
    public bool UsesDependencyGraph => Type == WorkspaceType.DotNetDependency;

    /// <summary>NuGet packages are discovered and matched against registries.</summary>
    public bool UsesNuGetPackages => Type == WorkspaceType.DotNetDependency;

    /// <summary>Update rewrites package versions and orders work by dependency level.</summary>
    public bool UsesDependencyAwareUpdate => Type == WorkspaceType.DotNetDependency;

    /// <summary>Push is ordered by dependency level and waits for package publication.</summary>
    public bool UsesDependencyAwarePush => Type == WorkspaceType.DotNetDependency;

    /// <summary>dotnet restore participates in workspace operations.</summary>
    public bool UsesPackageRestore => Type == WorkspaceType.DotNetDependency;

    /// <summary>
    /// Configured .csproj version files are additionally interpreted as generated NuGet package and
    /// dependency metadata. Generic file-version substitution and checking is available to every
    /// workspace type and is deliberately not covered by this capability.
    /// </summary>
    public bool UsesGeneratedPackagesFromVersionFiles => Type == WorkspaceType.DotNetDependency;

    /// <summary>Any CI integration is configured.</summary>
    public bool UsesCiIntegration => CiProvider != WorkspaceCiProvider.None;

    /// <summary>GitHub Actions specifically is the CI provider. Independent of GitHub source control.</summary>
    public bool UsesGitHubActions => CiProvider == WorkspaceCiProvider.GitHubActions;

    /// <summary>The Worker-facing subset, for App-initiated repository operations.</summary>
    public RepositoryOperationCapabilities ToRepositoryOperationCapabilities() =>
        RepositoryOperationCapabilities.For(UsesRepositoryVersioning, DiscoversDotNetProjects);

    /// <summary>What every workspace looked like before workspace profiles existed.</summary>
    public static WorkspaceCapabilities Legacy { get; } = new(
        WorkspaceType.DotNetDependency,
        WorkspaceVersioningMode.GitVersion,
        WorkspaceCiProvider.GitHubActions);
}
