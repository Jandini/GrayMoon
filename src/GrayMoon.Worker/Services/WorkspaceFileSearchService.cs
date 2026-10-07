using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

/// <summary>Recursive file search inside Git repositories that skips .git, nested repositories and everything Git excludes (see <see cref="IGitIgnoreSession.IsExcluded"/>). Supports * and ? in pattern.</summary>
public sealed class WorkspaceFileSearchService(IGitIgnoreService ignore) : IWorkspaceFileSearchService
{
    private static readonly StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;

    public Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspacePath,
        string? repositoryName,
        string searchPattern,
        string? workspaceRepositoryName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);

        var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();
        var results = new List<WorkspaceFileSearchResult>();

        IEnumerable<string> repoDirs;
        string? scopedRepositoryName = null;
        if (!string.IsNullOrWhiteSpace(repositoryName))
        {
            scopedRepositoryName = repositoryName.Trim();
            var single = WorkerRepositoryPaths.Resolve(workspacePath, scopedRepositoryName, workspaceRepositoryName);
            if (!Directory.Exists(single))
                return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);
            repoDirs = [single];
        }
        else
        {
            try
            {
                repoDirs = Directory.GetDirectories(workspacePath)
                    .Where(d => !string.Equals(Path.GetFileName(d), ".git", OrdinalIgnoreCase) && WorkerRepositoryPaths.HasGitMetadata(d));
            }
            catch
            {
                return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);
            }
        }

        foreach (var repoDir in repoDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The Workspace repository's folder is the workspace folder, so report the repository's own name.
            var repoName = scopedRepositoryName is not null
                && WorkerRepositoryPaths.IsWorkspaceRepository(scopedRepositoryName, workspaceRepositoryName)
                    ? scopedRepositoryName
                    : Path.GetFileName(repoDir);
            try
            {
                using var session = ignore.Open(repoDir);
                EnumerateMatchingFiles(session, repoDir, repoDir, relativeDir: "", repoName, pattern, results, cancellationToken);
            }
            catch (GitIgnoreException ex)
            {
                throw new InvalidOperationException($"File search failed for repository {repoName}: {ex.Message}", ex);
            }
        }

        return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>(results);
    }

    /// <summary>Recursively enumerates files under currentDir, skipping .git, nested repositories and Git-excluded paths. Adds matches (relative to repoRoot) to results.</summary>
    private static void EnumerateMatchingFiles(
        IGitIgnoreSession session,
        string repoRoot,
        string currentDir,
        string relativeDir,
        string repositoryName,
        string pattern,
        List<WorkspaceFileSearchResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (!MatchesPattern(fileName, pattern))
                    continue;
                var relativePath = relativeDir.Length == 0 ? fileName : relativeDir + "/" + fileName;
                if (session.IsExcluded(relativePath, GitPathKind.File))
                    continue;
                results.Add(new WorkspaceFileSearchResult
                {
                    RepositoryName = repositoryName,
                    FilePath = relativePath,
                    FileName = fileName
                });
            }

            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(subDir);
                if (string.Equals(dirName, ".git", OrdinalIgnoreCase))
                    continue;
                if (WorkerRepositoryPaths.HasGitMetadata(subDir))
                    continue; // nested repository: never part of this repository
                var relativeSub = relativeDir.Length == 0 ? dirName : relativeDir + "/" + dirName;
                if (session.IsExcluded(relativeSub, GitPathKind.Directory))
                    continue;
                EnumerateMatchingFiles(session, repoRoot, subDir, relativeSub, repositoryName, pattern, results, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read directory '{currentDir}': {ex.Message}", ex);
        }
    }

    /// <summary>Simple glob: * = any sequence, ? = single character.</summary>
    private static bool MatchesPattern(string fileName, string pattern)
    {
        return Matches(pattern.AsSpan(), fileName.AsSpan());

        static bool Matches(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
        {
            while (true)
            {
                if (p.IsEmpty)
                    return s.IsEmpty;
                if (p[0] == '*')
                {
                    p = p[1..];
                    if (p.IsEmpty)
                        return true;
                    for (var i = 0; i <= s.Length; i++)
                    {
                        if (Matches(p, s[i..]))
                            return true;
                    }
                    return false;
                }
                if (s.IsEmpty)
                    return false;
                if (p[0] == '?' || p[0] == s[0])
                {
                    p = p[1..];
                    s = s[1..];
                    continue;
                }
                return false;
            }
        }
    }
}
