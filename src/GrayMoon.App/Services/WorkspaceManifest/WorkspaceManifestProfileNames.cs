using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>Enum to manifest string mapping for the Workspace definition profile (schema v1).</summary>
public static class WorkspaceManifestProfileNames
{
    public static string ToManifest(WorkspaceType type) => type switch
    {
        WorkspaceType.Basic => "basic",
        WorkspaceType.DotNetDependency => "dotNetDependency",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static string ToManifest(WorkspaceVersioningMode mode) => mode switch
    {
        WorkspaceVersioningMode.None => "none",
        WorkspaceVersioningMode.GitVersion => "gitVersion",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static string ToManifest(WorkspaceCiProvider ci) => ci switch
    {
        WorkspaceCiProvider.None => "none",
        WorkspaceCiProvider.GitHubActions => "githubActions",
        _ => throw new ArgumentOutOfRangeException(nameof(ci), ci, null)
    };

    public static bool TryParse(string? value, out WorkspaceType type)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "basic": type = WorkspaceType.Basic; return true;
            case "dotnetdependency": type = WorkspaceType.DotNetDependency; return true;
            default: type = default; return false;
        }
    }

    public static bool TryParse(string? value, out WorkspaceVersioningMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "none": mode = WorkspaceVersioningMode.None; return true;
            case "gitversion": mode = WorkspaceVersioningMode.GitVersion; return true;
            default: mode = default; return false;
        }
    }

    public static bool TryParse(string? value, out WorkspaceCiProvider ci)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "none": ci = WorkspaceCiProvider.None; return true;
            case "githubactions": ci = WorkspaceCiProvider.GitHubActions; return true;
            default: ci = default; return false;
        }
    }
}
