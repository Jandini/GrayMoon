using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Which Host prerequisites the existing workspaces actually need. Git is always required; the .NET SDK
/// and GitVersion only when some workspace profile uses them. Tools that are not required are still probed
/// and still installable, but a missing one never marks the host as needing attention.
/// </summary>
/// <remarks>
/// GitVersion runs as <c>dotnet gitversion</c> (repository tool manifest, restored with
/// <c>dotnet tool restore</c>) or as the global <c>dotnet-gitversion</c> tool, and the host probe itself is
/// <c>dotnet gitversion version</c>. Every one of those goes through the .NET SDK, so GitVersion implies the
/// .NET SDK even for a Basic workspace.
/// </remarks>
public sealed record HostPrerequisiteRequirements(bool DotnetSdkRequired, bool GitVersionRequired)
{
    /// <summary>No workspace needs anything beyond Git (also the answer when there are no workspaces).</summary>
    public static HostPrerequisiteRequirements GitOnly { get; } = new(false, false);

    /// <summary>The pre-profile behaviour: every probed tool is required.</summary>
    public static HostPrerequisiteRequirements All { get; } = new(true, true);

    public bool IsRequired(string id) => id switch
    {
        HostPrerequisiteIds.Git => true,
        HostPrerequisiteIds.DotnetSdk => DotnetSdkRequired,
        HostPrerequisiteIds.GitVersion => GitVersionRequired,
        _ => false
    };

    public static HostPrerequisiteRequirements For(WorkspaceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new HostPrerequisiteRequirements(
            DotnetSdkRequired: capabilities.UsesGitVersion
                || capabilities.UsesPackageRestore
                || capabilities.UsesDependencyAwareUpdate,
            GitVersionRequired: capabilities.UsesGitVersion);
    }

    /// <summary>The union over every workspace on this GrayMoon. No workspaces means Git only.</summary>
    public static HostPrerequisiteRequirements For(IEnumerable<WorkspaceCapabilities> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var result = GitOnly;
        foreach (var workspace in workspaces)
        {
            var required = For(workspace);
            result = new HostPrerequisiteRequirements(
                result.DotnetSdkRequired || required.DotnetSdkRequired,
                result.GitVersionRequired || required.GitVersionRequired);
        }

        return result;
    }
}
