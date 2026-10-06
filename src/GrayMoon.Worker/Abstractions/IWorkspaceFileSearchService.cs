using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>Finds files in workspace repositories with wildcard support, skipping .git, bin, and obj at any depth.</summary>
public interface IWorkspaceFileSearchService
{
    /// <summary>
    /// Searches for files matching the pattern in the given workspace, optionally limited to one repository.
    /// Skips .git, bin, and obj directories at any depth. Pattern supports * and ? (e.g. "*.cs").
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
