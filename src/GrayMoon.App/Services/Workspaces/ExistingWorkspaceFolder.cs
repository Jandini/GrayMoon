using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// What Add Workspace shows and selects when the folder for the new name is already on disk.
/// </summary>
public static class ExistingWorkspaceFolder
{
    public static string Callout(bool isWorkspaceRepository, bool workspaceRepositoryImported, int repositoryCount)
    {
        var repositories = $"{repositoryCount} {(repositoryCount == 1 ? "repository" : "repositories")}";
        if (!isWorkspaceRepository)
            return $"The workspace directory exists with {repositories}.";

        if (!workspaceRepositoryImported && repositoryCount == 0)
            return "The workspace directory exists and is a workspace repository that is not imported yet.";

        if (!workspaceRepositoryImported)
            return $"The workspace directory exists and is a workspace repository that is not imported yet, with {repositories}.";

        if (repositoryCount == 0)
            return "The workspace directory exists and is a workspace repository.";

        return $"The workspace directory exists and is a workspace repository, with {repositories}.";
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
