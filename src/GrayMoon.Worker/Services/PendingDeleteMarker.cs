using System.Globalization;
using System.Text;

namespace GrayMoon.Worker.Services;

/// <summary>
/// The <c>GRAYMOON-PENDING-DELETE.md</c> file left in a removed Feature's folder when files were still in use. The top is
/// plain text for a person or an AI agent; the comment block at the bottom is what the background cleanup reads.
/// </summary>
public sealed record PendingDeleteMarker(string Folder, string? Workspace, string? Feature, DateTime MarkedUtc)
{
    public const string FileName = "GRAYMOON-PENDING-DELETE.md";

    private const string BlockStart = "<!-- graymoon-pending-delete";
    private const string BlockEnd = "-->";

    public string Render()
    {
        var feature = string.IsNullOrWhiteSpace(Feature) ? "a GrayMoon Feature" : $"the GrayMoon Feature \"{Feature}\"";
        var workspace = string.IsNullOrWhiteSpace(Workspace) ? "" : $" in workspace \"{Workspace}\"";
        var marked = MarkedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.AppendLine("# This folder is pending deletion");
        text.AppendLine();
        text.AppendLine($"This folder belonged to {feature}{workspace}. The Feature was removed on {marked} UTC, but some files");
        text.AppendLine("were still in use, so the folder could not be deleted completely.");
        text.AppendLine();
        text.AppendLine("GrayMoon will delete this whole folder automatically later. Do not save work here: nothing in this folder is a Git");
        text.AppendLine("worktree any more, and anything left here will be deleted.");
        text.AppendLine();
        text.AppendLine("If you are an AI agent working in this folder: stop and tell the user this Feature was removed.");
        text.AppendLine();
        text.AppendLine(BlockStart);
        text.AppendLine("version: 1");
        if (!string.IsNullOrWhiteSpace(Workspace))
            text.AppendLine($"workspace: {OneLine(Workspace)}");
        if (!string.IsNullOrWhiteSpace(Feature))
            text.AppendLine($"feature: {OneLine(Feature)}");
        text.AppendLine($"folder: {OneLine(Folder)}");
        text.AppendLine($"markedUtc: {MarkedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}");
        text.AppendLine(BlockEnd);
        return text.ToString();
    }

    /// <summary>Reads the comment block; null when there is none or it has no <c>folder</c>.</summary>
    public static PendingDeleteMarker? Parse(string content)
    {
        var start = content.IndexOf(BlockStart, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = content.IndexOf(BlockEnd, start + BlockStart.Length, StringComparison.Ordinal);
        if (end < 0)
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content[(start + BlockStart.Length)..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!values.TryGetValue("folder", out var folder) || string.IsNullOrWhiteSpace(folder))
            return null;

        var markedUtc = values.TryGetValue("markedUtc", out var marked)
            && DateTime.TryParse(marked, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;

        return new PendingDeleteMarker(
            folder,
            values.GetValueOrDefault("workspace"),
            values.GetValueOrDefault("feature"),
            markedUtc);
    }

    /// <summary>Reads the marker at the top of <paramref name="folder"/>; null when it is missing or unreadable.</summary>
    public static PendingDeleteMarker? TryRead(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a value on one line and out of the comment terminator, so the block always parses back.</summary>
    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace(BlockEnd, "- ->", StringComparison.Ordinal).Trim();
}
