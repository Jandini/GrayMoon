using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>Finds files in workspace Git repositories with wildcard support, skipping .git, nested repositories and Git-excluded paths.</summary>
public interface IWorkspaceFileSearchService
{
    /// <summary>
    /// Searches for files matching the pattern in the given workspace, optionally limited to one repository.
    /// Skips .git, nested repositories and Git-excluded (ignored, untracked) paths; only directories with Git metadata are searched. Pattern supports * and ? (e.g. "*.cs").
    /// When <paramref name="repositoryName"/> equals <paramref name="workspaceRepositoryName"/> the search runs in the
    /// workspace folder itself (the Workspace repository's working tree) and still skips nested repositories.
    /// </summary>
    Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspacePath,
        string? repositoryName,
        string searchPattern,
        string? workspaceRepositoryName = null,
        CancellationToken cancellationToken = default);
}
