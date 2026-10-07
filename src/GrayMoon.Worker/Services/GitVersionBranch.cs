using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

/// <summary>
/// GitVersion is allowed to fail (an empty repository, a path over the Windows limit, a broken config), and a repository
/// must not lose its identity when it does. The branch is a plain git fact, so it is read from git itself whenever
/// GitVersion did not give one.
/// </summary>
internal static class GitVersionBranch
{
    /// <summary>
    /// The branch name from GitVersion's output when it ran, otherwise from <c>git branch --show-current</c>.
    /// Null for a detached HEAD, which callers already treat as "no branch".
    /// </summary>
    public static async Task<string?> ResolveBranchAsync(
        this IGitRepositoryReader reader,
        GitVersionResult? versionResult,
        string repoPath,
        CancellationToken cancellationToken)
    {
        var fromGitVersion = Choose(versionResult, null);
        if (!string.IsNullOrWhiteSpace(fromGitVersion))
            return fromGitVersion;

        return Choose(null, await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken));
    }

    /// <summary>
    /// Picks the branch name: GitVersion's when it returned a non-blank one, otherwise <paramref name="gitBranch"/>
    /// (read from git). An empty GitVersion <c>BranchName</c> falls through to git, not to <c>EscapedBranchName</c>.
    /// </summary>
    public static string? Choose(GitVersionResult? versionResult, string? gitBranch)
    {
        var fromGitVersion = versionResult?.BranchName ?? versionResult?.EscapedBranchName;
        return !string.IsNullOrWhiteSpace(fromGitVersion) ? fromGitVersion : gitBranch;
    }
}
