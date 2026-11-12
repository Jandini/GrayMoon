namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Tooltips of the <c>archived</c> and <c>ro</c> badges. Both states stop writes, so each tooltip says who decided:
/// the provider (archived) or the user in GrayMoon (read-only).
/// </summary>
public static class RepositoryBadgeTitles
{
    public const string Archived = "Archived on the provider. Pushes are rejected.";

    public const string Reference = "Read-only in this Workspace. GrayMoon does not commit, push or update it.";

    public const string ReferenceSnapshot = "Read-only in this Feature. GrayMoon does not commit, push or update it.";

    public const string ReferenceInGitChanges =
        "Read-only in this Workspace. GrayMoon does not commit or push it, and actions on all repositories leave it out.";
}
