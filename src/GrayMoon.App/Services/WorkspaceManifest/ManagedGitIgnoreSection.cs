using System.Text;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>
/// Maintains the GrayMoon-managed section of the Workspace repository <c>.gitignore</c> (D12). Lines
/// outside the section are preserved byte for byte; the section itself is emitted with LF line endings.
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

        var section = BuildSection(sourceRepositoryNames, ignoreCodeGraph);
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
            return section + "\n";

        var sb = new StringBuilder(content);
        if (content[^1] != '\n')
            sb.Append('\n');
        if (!EndsWithBlankLine(sb))
            sb.Append('\n');
        sb.Append(section).Append('\n');
        return sb.ToString();
    }

    private static string BuildSection(IEnumerable<string> names, bool ignoreCodeGraph)
    {
        var sb = new StringBuilder();
        sb.Append(StartMarker).Append('\n');
        foreach (var name in names
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('/').Append(name).Append("/\n");
        }

        if (ignoreCodeGraph)
            sb.Append(CodeGraphEntry).Append('\n');

        sb.Append(EndMarker);
        return sb.ToString();
    }

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