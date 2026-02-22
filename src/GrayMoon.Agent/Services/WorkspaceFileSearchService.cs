using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

/// <summary>Recursive file search that skips .git, bin, and obj at any depth. Supports * and ? in pattern.</summary>
public sealed class WorkspaceFileSearchService : IWorkspaceFileSearchService
{
    private static readonly StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;

    private static bool IsSkippedDirectory(string dirName)
    {
        return string.Equals(dirName, ".git", OrdinalIgnoreCase)
            || string.Equals(dirName, "bin", OrdinalIgnoreCase)
            || string.Equals(dirName, "obj", OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspacePath,
        string? repositoryName,
        string searchPattern,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);

        var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();
        var results = new List<WorkspaceFileSearchResult>();

        IEnumerable<string> repoDirs;
        if (!string.IsNullOrWhiteSpace(repositoryName))
        {
            var single = Path.Combine(workspacePath, repositoryName.Trim());
            if (!Directory.Exists(single))
                return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);
            repoDirs = [single];
        }
        else
        {
            try
            {
                repoDirs = Directory.GetDirectories(workspacePath)
                    .Where(d => !string.Equals(Path.GetFileName(d), ".git", OrdinalIgnoreCase));
            }
            catch
            {
                return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]);
            }
        }

        foreach (var repoDir in repoDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repoName = Path.GetFileName(repoDir);
            EnumerateMatchingFiles(repoDir, repoDir, repoName, pattern, results, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>(results);
    }

    /// <summary>Recursively enumerates files under currentDir, skipping .git, bin, obj. Adds matches (relative to repoRoot) to results.</summary>
    private static void EnumerateMatchingFiles(
        string repoRoot,
        string currentDir,
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
                var relativePath = Path.GetRelativePath(repoRoot, file);
                results.Add(new WorkspaceFileSearchResult
                {
                    RepositoryName = repositoryName,
                    FilePath = relativePath.Replace('\\', '/'),
                    FileName = fileName
                });
            }

            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(subDir);
                if (IsSkippedDirectory(dirName))
                    continue;
                EnumerateMatchingFiles(repoRoot, subDir, repositoryName, pattern, results, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Skip directories we can't read
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
