using System.Text;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>
/// Maintains the GrayMoon-managed section of the Workspace repository <c>.gitignore</c> (D12). Lines
/// outside the section are preserved byte for byte. An existing file that uses CRLF keeps it; new
/// files and LF files get LF.
/// </summary>
public static class ManagedGitIgnoreSection
{
    public const string StartMarker = "# <graymoon-repositories>";
    public const string EndMarker = "# </graymoon-repositories>";

    /// <summary>The CodeGraph data folder, ignored as a whole when CodeGraph is on (index, database and its own .gitignore).</summary>
    public const string CodeGraphEntry = "/.codegraph/";

    public static string Apply(string? existingContent, IEnumerable<string> sourceRepositoryNames, bool ignoreCodeGraph = false)
    {
        ArgumentNullException.ThrowIfNull(sourceRepositoryNames);

        var newline = Newline(existingContent);
        var section = BuildSection(sourceRepositoryNames, ignoreCodeGraph, newline);
        var content = existingContent ?? string.Empty;

        var start = FindMarkerAtLineStart(content, StartMarker, 0);
        if (start >= 0)
        {
            var end = FindMarkerAtLineStart(content, EndMarker, start + StartMarker.Length);
            if (end >= 0)
            {
                var afterEnd = end + EndMarker.Length;
                return string.Concat(content.AsSpan(0, start), section, content.AsSpan(afterEnd));
            }
        }

        if (content.Length == 0)
            return section + newline;

        var sb = new StringBuilder(content);
        if (content[^1] != '\n')
            sb.Append(newline);
        if (!EndsWithBlankLine(sb))
            sb.Append(newline);
        sb.Append(section).Append(newline);
        return sb.ToString();
    }

    private static string BuildSection(IEnumerable<string> names, bool ignoreCodeGraph, string newline)
    {
        var sb = new StringBuilder();
        sb.Append(StartMarker).Append(newline);
        foreach (var name in names
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('/').Append(name).Append('/').Append(newline);
        }

        if (ignoreCodeGraph)
            sb.Append(CodeGraphEntry).Append(newline);

        sb.Append(EndMarker);
        return sb.ToString();
    }

    private static string Newline(string? content) =>
        content is not null && content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int FindMarkerAtLineStart(string content, string marker, int from)
    {
        var index = from;
        while (index <= content.Length - marker.Length)
        {
            index = content.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0)
                return -1;
            if (index == 0 || content[index - 1] == '\n')
                return index;
            index += marker.Length;
        }

        return -1;
    }

    // Caller guarantees the builder ends with a line feed.
    private static bool EndsWithBlankLine(StringBuilder sb)
    {
        var i = sb.Length - 2;
        if (i >= 0 && sb[i] == '\r')
            i--;
        return i < 0 || sb[i] == '\n';
    }
}