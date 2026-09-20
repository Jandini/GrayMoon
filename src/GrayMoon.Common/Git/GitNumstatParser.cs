namespace GrayMoon.Common.Git;

/// <summary>Totals from <c>git diff --numstat</c> (added/deleted columns only).</summary>
public readonly record struct GitNumstatTotals(int Insertions, int Deletions);

/// <summary>
/// Pure parser for <c>git diff --numstat</c> output. Each line is
/// <c>added&lt;tab&gt;deleted&lt;tab&gt;path</c>. Binary rows use <c>-</c> and are skipped.
/// </summary>
public static class GitNumstatParser
{
    public static GitNumstatTotals Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new GitNumstatTotals(0, 0);
        }

        var insertions = 0;
        var deletions = 0;
        var span = output.AsSpan();
        while (!span.IsEmpty)
        {
            var newline = span.IndexOf('\n');
            ReadOnlySpan<char> line;
            if (newline < 0)
            {
                line = span;
                span = [];
            }
            else
            {
                line = span[..newline];
                span = span[(newline + 1)..];
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty)
            {
                continue;
            }

            var firstTab = line.IndexOf('\t');
            if (firstTab <= 0)
            {
                continue;
            }

            var secondTab = line[(firstTab + 1)..].IndexOf('\t');
            var addedSpan = line[..firstTab];
            var deletedSpan = secondTab < 0
                ? line[(firstTab + 1)..]
                : line.Slice(firstTab + 1, secondTab);

            if (addedSpan is "-" || deletedSpan is "-")
            {
                continue;
            }

            if (!int.TryParse(addedSpan, out var added) || !int.TryParse(deletedSpan, out var deleted))
            {
                continue;
            }

            insertions += added;
            deletions += deleted;
        }

        return new GitNumstatTotals(insertions, deletions);
    }
}
