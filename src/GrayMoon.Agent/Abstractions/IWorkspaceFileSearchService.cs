using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

/// <summary>Finds files in workspace repositories with wildcard support, skipping .git, bin, and obj at any depth.</summary>
public interface IWorkspaceFileSearchService
{
    /// <summary>
    /// Searches for files matching the pattern in the given workspace, optionally limited to one repository.
    /// Skips .git, bin, and obj directories at any depth. Pattern supports * and ? (e.g. "*.cs").
    /// </summary>
    Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspacePath,
        string? repositoryName,
        string searchPattern,
        CancellationToken cancellationToken = default);
}
