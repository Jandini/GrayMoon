using GrayMoon.Agent.Services.GitChanges;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Services;

/// <summary>
/// Git-native ignore handling. Used as an add fallback (after Git refuses ignored pathspecs)
/// and to skip ignored top-level directories when scanning csproj files. Never force-adds (<c>-f</c>).
/// </summary>
internal static class GitIgnoredPathFilter
{
    internal const string IgnoredByGitignoreMarker = "ignored by one of your .gitignore files";

    internal static bool IsIgnoredPathsAddError(string? stderr, string? stdout)
    {
        if (!string.IsNullOrEmpty(stderr) && stderr.Contains(IgnoredByGitignoreMarker, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(stdout) && stdout.Contains(IgnoredByGitignoreMarker, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Runs <c>git add</c> as supplied. On ignored-pathspec failure only, drops ignored paths via
    /// <c>git check-ignore -z --stdin</c> (no <c>--no-index</c>, no <c>-f</c>) and retries the rest.
    /// An empty remaining list is treated as success (nothing to add).
    /// </summary>
    internal static async Task<(int ExitCode, string? Stdout, string? Stderr)> AddWithIgnoredFallbackAsync(
        GitProcessRunner runner,
        ILogger logger,
        string repoPath,
        IReadOnlyList<string> paths,
        Func<IReadOnlyList<string>, Task<(int ExitCode, string? Stdout, string? Stderr)>> addAsync,
        CancellationToken cancellationToken)
    {
        var first = await addAsync(paths);
        if (first.ExitCode == 0)
            return first;

        if (!IsIgnoredPathsAddError(first.Stderr, first.Stdout))
            return first;

        var remaining = await KeepNonIgnoredAsync(runner, logger, repoPath, paths, cancellationToken);
        if (remaining.Count == 0)
            return (0, null, null);

        if (remaining.Count == paths.Count)
            return first;

        logger.LogInformation(
            "Retrying git add for {RepoPath} after dropping {DroppedCount} gitignored path(s)",
            repoPath,
            paths.Count - remaining.Count);

        return await addAsync(remaining);
    }

    /// <summary>
    /// Returns the subset of <paramref name="paths"/> that are not ignored. On check-ignore failure,
    /// returns the original list so the caller can keep the original git error.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> KeepNonIgnoredAsync(
        GitProcessRunner runner,
        ILogger logger,
        string repoPath,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return paths;

        var stdinBytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(paths);
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            ["check-ignore", "-z", "--stdin"],
            repoPath,
            stdinBytes,
            cancellationToken,
            GitLockIntent.Read);

        // Exit 1: none of the paths are ignored.
        if (exitCode == 1)
            return paths;

        if (exitCode != 0)
        {
            logger.LogDebug(
                "git check-ignore failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}",
                repoPath, exitCode, stderr);
            return paths;
        }

        var ignored = ParseNulDelimited(stdout)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ignored.Count == 0)
            return paths;

        var kept = new List<string>(paths.Count);
        var dropped = new List<string>();
        foreach (var path in paths)
        {
            if (ignored.Contains(NormalizePath(path)))
                dropped.Add(path);
            else
                kept.Add(path);
        }

        if (dropped.Count > 0)
        {
            logger.LogInformation(
                "Skipping gitignored path(s) in {RepoPath}: {Paths}",
                repoPath,
                string.Join(", ", dropped));
        }

        return kept;
    }

    private static IEnumerable<string> ParseNulDelimited(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim();
}
