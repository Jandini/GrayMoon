namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Display name for the workspace git repository row, which also shows a Workspace badge.
/// A trailing <c>.Workspace</c> or <c>-workspace</c> is omitted so the badge does not repeat the suffix.
/// </summary>
public static class WorkspaceRepositoryDisplayName
{
    public static string ForWorkspaceRole(string? repositoryName)
    {
        if (string.IsNullOrEmpty(repositoryName))
            return string.Empty;

        if (TryStripSuffix(repositoryName, ".Workspace", out var stripped)
            || TryStripSuffix(repositoryName, "-workspace", out stripped))
            return stripped;

        return repositoryName;
    }

    private static bool TryStripSuffix(string name, string suffix, out string stripped)
    {
        if (name.Length > suffix.Length
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stripped = name[..^suffix.Length];
            return stripped.Length > 0;
        }

        stripped = name;
        return false;
    }
}
