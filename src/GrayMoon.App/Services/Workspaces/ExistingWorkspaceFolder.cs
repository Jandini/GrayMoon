using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Rules for a folder that is already on disk. Add Workspace only adds a new Workspace, so it refuses a folder with
/// content; such a folder is brought in with Import from disk, which matches its repositories to the imported ones.
/// </summary>
public static class ExistingWorkspaceFolder
{
    public const string NotEmptyMessage =
        "This folder already exists and is not empty. Use Import from disk to add it as a Workspace.";

    public const string CheckFailedMessage =
        "The Workspace folder could not be checked. Make sure the GrayMoon Worker is running and try again.";

    /// <summary>
    /// Why Add Workspace cannot use the folder, or null when it can (the folder does not exist, or is empty). A folder
    /// that could not be checked is blocked too: it is never assumed to be free.
    /// </summary>
    public static string? AddBlockedReason(WorkspaceDirectoryState state)
    {
        if (state.Error is not null)
            return $"Could not check the Workspace folder: {state.Error}";

        return state.Exists && state.IsEmpty != true ? NotEmptyMessage : null;
    }

    /// <summary>
    /// The imported repository whose clone URL is the same repository as <paramref name="originUrl"/>.
    /// Comparison uses <see cref="RepositoryUrlIdentity"/>, so a trailing <c>.git</c> or a different protocol still matches.
    /// </summary>
    public static int? MatchRepositoryId(string? originUrl, IEnumerable<(int RepositoryId, string CloneUrl)> catalog)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            return null;

        foreach (var (repositoryId, cloneUrl) in catalog)
        {
            if (RepositoryUrlIdentity.RepositoryUrlsEqual(cloneUrl, originUrl))
                return repositoryId;
        }

        return null;
    }
}
