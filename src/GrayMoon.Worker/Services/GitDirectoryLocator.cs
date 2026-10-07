using GrayMoon.Common.Git;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Locates a working tree's git directory and the worktree-private GrayMoon files inside it. Shared by the
/// reader (which reads the Feature divergence base) and <see cref="GitService"/> (which writes it).
/// </summary>
internal static class GitDirectoryLocator
{
    /// <summary>
    /// Worktree-private file (not the common git dir) so Feature worktrees keep their own parent-branch base.
    /// </summary>
    internal static async Task<string?> ResolveDivergenceBaseFilePathAsync(GitProcessRunner runner, string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        // Where the git directory is does not need a process in the normal layouts: it is the .git folder, or
        // the folder a linked worktree's .git file points at. Anything else asks git, as before.
        var fullGitDir = TryReadGitDirFromWorkTree(repoPath);
        if (fullGitDir is null)
        {
            var (exitCode, stdout, _) = await runner.RunAsync(
                "git",
                "rev-parse --git-dir",
                repoPath,
                ct,
                streamStderrAsStdout: true,
                mirrorFailureOutputAsStderr: false,
                intent: GitLockIntent.Read);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            var gitDir = stdout.Trim();
            fullGitDir = Path.IsPathRooted(gitDir)
                ? gitDir
                : Path.GetFullPath(Path.Combine(repoPath, gitDir));
        }

        return Path.Combine(fullGitDir, "graymoon-divergence-base");
    }

    /// <summary>
    /// The git directory of the working tree at <paramref name="repoPath"/> (what <c>git rev-parse --git-dir</c>
    /// prints), read from the file system: <c>.git</c> itself when it is a folder, or the target of its
    /// <c>gitdir:</c> line when it is a file (a linked worktree or a submodule). Null whenever that is not
    /// plainly the case - no <c>.git</c> entry, an unreadable or unfamiliar file, a target without a <c>HEAD</c>,
    /// or <c>GIT_DIR</c> set in the environment - so the caller asks git instead of guessing. Read on every call,
    /// never cached, so a worktree that is removed and added again is never answered from a stale path.
    /// </summary>
    internal static string? TryReadGitDirFromWorkTree(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_DIR")))
            return null;

        try
        {
            var dotGit = Path.Combine(repoPath, ".git");
            string candidate;
            if (Directory.Exists(dotGit))
            {
                candidate = Path.GetFullPath(dotGit);
            }
            else if (File.Exists(dotGit))
            {
                const string prefix = "gitdir:";
                var first = File.ReadLines(dotGit).FirstOrDefault();
                if (first is null || !first.StartsWith(prefix, StringComparison.Ordinal))
                    return null;

                var target = first[prefix.Length..].Trim();
                if (target.Length == 0)
                    return null;

                candidate = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target));
            }
            else
            {
                return null;
            }

            // A real git directory always has a HEAD; an empty or foreign .git folder is not one, and git itself
            // would look further up the tree for the repository.
            return File.Exists(Path.Combine(candidate, "HEAD")) ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
