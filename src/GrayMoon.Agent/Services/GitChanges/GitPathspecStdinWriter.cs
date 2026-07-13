using System.Text;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Builds NUL-delimited UTF-8 stdin payloads for <c>git add/restore --pathspec-from-file=- --pathspec-file-nul</c>
/// (avoids Windows command-line length limits and shell-quoting issues for large or unusual path sets), and
/// provides a bounded-batch fallback (plain <c>--</c> argument list) for git versions older than 2.25, which
/// introduced <c>--pathspec-from-file</c>.
/// </summary>
public static class GitPathspecStdinWriter
{
    private const int DefaultMaxBatchCharacters = 20_000;

    /// <summary>Encodes paths as UTF-8, NUL-separated, with a trailing NUL - the exact payload
    /// <c>--pathspec-file-nul</c> expects on stdin.</summary>
    public static byte[] BuildNulDelimitedUtf8(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var joined = string.Join('\0', paths) + '\0';
        return Encoding.UTF8.GetBytes(joined);
    }

    /// <summary>
    /// Splits paths into batches whose total encoded character length stays under <paramref name="maxBatchCharacters"/>,
    /// for the plain-argument-list compatibility fallback. Never splits a single path across batches, so a single
    /// path longer than the limit still produces its own (oversized) batch rather than being dropped.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> BuildBoundedBatches(
        IReadOnlyList<string> paths,
        int maxBatchCharacters = DefaultMaxBatchCharacters)
    {
        var batches = new List<IReadOnlyList<string>>();
        if (paths.Count == 0)
        {
            return batches;
        }

        var current = new List<string>();
        var currentLength = 0;

        foreach (var path in paths)
        {
            var pathLength = path.Length + 1; // +1 for the separating space in the eventual argument list
            if (current.Count > 0 && currentLength + pathLength > maxBatchCharacters)
            {
                batches.Add(current);
                current = [];
                currentLength = 0;
            }

            current.Add(path);
            currentLength += pathLength;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>Whether the installed git (per <c>git --version</c> stdout) supports <c>--pathspec-from-file</c> (git &gt;= 2.25).</summary>
    public static bool SupportsPathspecFromFile(string? gitVersionOutput)
    {
        var version = ParseGitVersion(gitVersionOutput);
        return version != null && version >= new Version(2, 25);
    }

    private static Version? ParseGitVersion(string? gitVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(gitVersionOutput))
        {
            return null;
        }

        var text = gitVersionOutput.Trim();
        const string prefix = "git version ";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        var firstToken = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;

        // Distro builds append suffixes like "2.43.0.windows.1" - only the first three numeric
        // components are meaningful for the >= 2.25 comparison.
        var parts = firstToken.Split('.').Take(3).ToList();
        if (parts.Count < 2)
        {
            return null;
        }

        var numeric = new int[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            if (!int.TryParse(parts[i], out numeric[i]))
            {
                return null;
            }
        }

        return numeric.Length == 2
            ? new Version(numeric[0], numeric[1])
            : new Version(numeric[0], numeric[1], numeric[2]);
    }
}
