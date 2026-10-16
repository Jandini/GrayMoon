using System.Text;

namespace GrayMoon.Common.Ai;

/// <summary>One file from <c>git diff --raw --numstat -z</c>.</summary>
public sealed record GitDiffInventoryEntry(
    string Path,
    string? OldPath,
    GitEvidenceChange Change,
    int? Added,
    int? Deleted,
    bool IsSubmodule,
    bool IsSymlink)
{
    public bool IsBinary => Added is null && Deleted is null && !IsSubmodule;
}

/// <summary>
/// Process-free parser for <c>git diff --raw --numstat -z [-M]</c>: NUL-separated raw records
/// (<c>:mode mode sha sha STATUS\0path\0</c>, two paths for renames and copies) followed by numstat records
/// (<c>added\tdeleted\tpath\0</c>, or <c>added\tdeleted\t\0old\0new\0</c> for renames). Binary files have <c>-</c> counts.
/// </summary>
public static class GitDiffInventoryParser
{
    private const string SubmoduleMode = "160000";
    private const string SymlinkMode = "120000";

    public static IReadOnlyList<GitDiffInventoryEntry> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var tokens = output.Split('\0');
        var raw = new List<(string Path, string? OldPath, GitEvidenceChange Change, bool IsSubmodule, bool IsSymlink)>();
        var stats = new Dictionary<string, (int? Added, int? Deleted)>(StringComparer.Ordinal);

        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                i++;
                continue;
            }

            if (token[0] == ':')
            {
                var fields = token[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5 || i + 1 >= tokens.Length)
                    break;

                var status = fields[4];
                var change = ToChange(status[0]);
                var isSubmodule = fields[0] == SubmoduleMode || fields[1] == SubmoduleMode;
                var isSymlink = fields[0] == SymlinkMode || fields[1] == SymlinkMode;
                if (change is GitEvidenceChange.Renamed or GitEvidenceChange.Copied)
                {
                    if (i + 2 >= tokens.Length)
                        break;
                    raw.Add((tokens[i + 2], tokens[i + 1], change, isSubmodule, isSymlink));
                    i += 3;
                }
                else
                {
                    raw.Add((tokens[i + 1], null, change, isSubmodule, isSymlink));
                    i += 2;
                }

                continue;
            }

            var firstTab = token.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : token.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                i++;
                continue;
            }

            var added = ParseCount(token[..firstTab]);
            var deleted = ParseCount(token[(firstTab + 1)..secondTab]);
            var path = token[(secondTab + 1)..];
            if (path.Length == 0)
            {
                // Rename/copy: the next two tokens are the old and new path.
                if (i + 2 >= tokens.Length)
                    break;
                stats[tokens[i + 2]] = (added, deleted);
                i += 3;
            }
            else
            {
                stats[path] = (added, deleted);
                i++;
            }
        }

        var entries = new List<GitDiffInventoryEntry>(raw.Count);
        foreach (var (path, oldPath, change, isSubmodule, isSymlink) in raw)
        {
            stats.TryGetValue(path, out var stat);
            entries.Add(new GitDiffInventoryEntry(path, oldPath, change, stat.Added, stat.Deleted, isSubmodule, isSymlink));
        }

        return entries;
    }

    private static int? ParseCount(string value) => int.TryParse(value, out var count) ? count : null;

    private static GitEvidenceChange ToChange(char status) => status switch
    {
        'A' => GitEvidenceChange.Added,
        'D' => GitEvidenceChange.Deleted,
        'R' => GitEvidenceChange.Renamed,
        'C' => GitEvidenceChange.Copied,
        'T' => GitEvidenceChange.TypeChanged,
        'U' => GitEvidenceChange.Unmerged,
        _ => GitEvidenceChange.Modified,
    };
}

/// <summary>
/// Splits a multi-file unified diff into per-file hunk text and trims each to a byte budget at line boundaries. Headers
/// (<c>diff --git</c>, <c>index</c>, <c>---</c>/<c>+++</c>, mode and rename lines) are dropped: the inventory already
/// says what happened to the file, so only <c>@@</c> hunks and their lines are kept.
/// </summary>
public static class UnifiedDiffSplitter
{
    /// <summary>
    /// Hunk text per new path for the files in <paramref name="expected"/> (new path to old path). A section whose header
    /// does not match an expected pair exactly (for example a C-quoted path) is skipped rather than guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Split(string? diff, IReadOnlyDictionary<string, string?> expected)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(diff) || expected.Count == 0)
            return result;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, oldPath) in expected)
            headers[$"diff --git a/{oldPath ?? path} b/{path}"] = path;

        string? current = null;
        StringBuilder? body = null;
        var inHunks = false;

        void Flush()
        {
            if (current is not null && body is { Length: > 0 })
                result[current] = body.ToString();
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                current = headers.TryGetValue(line, out var path) ? path : null;
                body = current is null ? null : new StringBuilder();
                inHunks = false;
                continue;
            }

            if (body is null)
                continue;

            if (line.StartsWith("@@", StringComparison.Ordinal))
                inHunks = true;

            if (!inHunks)
                continue;

            body.Append(line).Append('\n');
        }

        Flush();
        return result;
    }

    /// <summary>
    /// The first lines of <paramref name="hunks"/> that fit in <paramref name="maxBytes"/> UTF-8 bytes, cut at a line
    /// boundary. <paramref name="truncated"/> is true when anything was left out.
    /// </summary>
    public static string Trim(string hunks, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (Encoding.UTF8.GetByteCount(hunks) <= maxBytes)
            return hunks;

        truncated = true;
        var builder = new StringBuilder();
        var used = 0;
        foreach (var line in hunks.Split('\n'))
        {
            var cost = Encoding.UTF8.GetByteCount(line) + 1;
            if (used + cost > maxBytes)
            {
                // A single very long line (minified code) still contributes its beginning.
                const string ellipsis = " ...\n";
                if (builder.Length == 0 && maxBytes > 80)
                    builder.Append(TruncateUtf8(line, maxBytes - ellipsis.Length)).Append(ellipsis);
                break;
            }

            builder.Append(line).Append('\n');
            used += cost;
        }

        return builder.ToString();
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > maxBytes)
                break;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
