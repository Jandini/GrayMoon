using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GrayMoon.App.Services;

public enum GhaLogLineKind { Normal, Error, Warning, Command, Debug }

public abstract record GhaLogEntry;

public record GhaLogLineEntry(GhaLogLineKind Kind, string HtmlContent) : GhaLogEntry;

public record GhaLogGroupEntry(string Title, bool HasError, List<GhaLogLineEntry> Lines) : GhaLogEntry;

public static partial class GhaLogParser
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z ", RegexOptions.None)]
    private static partial Regex TimestampRx();

    [GeneratedRegex(@"\x1B\[([0-9;]*)m", RegexOptions.None)]
    private static partial Regex AnsiRx();

    public static List<GhaLogEntry> ParseJobLog(string rawLog)
    {
        var entries = new List<GhaLogEntry>();
        if (string.IsNullOrEmpty(rawLog)) return entries;

        var lines = rawLog.Split('\n');

        string? groupTitle = null;
        bool groupHasError = false;
        var groupLines = new List<GhaLogLineEntry>();

        void FlushGroup()
        {
            if (groupTitle == null) return;
            entries.Add(new GhaLogGroupEntry(groupTitle, groupHasError, [.. groupLines]));
            groupTitle = null;
            groupHasError = false;
            groupLines.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var content = TimestampRx().Replace(line, "", 1);

            if (content.Equals("##[endgroup]", StringComparison.OrdinalIgnoreCase))
            {
                FlushGroup();
                continue;
            }

            if (content.StartsWith("##[group]", StringComparison.OrdinalIgnoreCase))
            {
                FlushGroup();
                groupTitle = WebUtility.HtmlEncode(content["##[group]".Length..]);
                continue;
            }

            var (kind, html) = ParseLineContent(content);

            if (groupTitle != null)
            {
                if (kind == GhaLogLineKind.Error) groupHasError = true;
                groupLines.Add(new GhaLogLineEntry(kind, html));
            }
            else
            {
                entries.Add(new GhaLogLineEntry(kind, html));
            }
        }

        FlushGroup();
        return entries;
    }

    private static (GhaLogLineKind Kind, string Html) ParseLineContent(string content)
    {
        if (content.StartsWith("##[error]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Error, ProcessAnsiColors(content["##[error]".Length..]));
        if (content.StartsWith("##[warning]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Warning, ProcessAnsiColors(content["##[warning]".Length..]));
        if (content.StartsWith("##[command]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Command, ProcessAnsiColors(content["##[command]".Length..]));
        if (content.StartsWith("##[debug]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Debug, ProcessAnsiColors(content["##[debug]".Length..]));

        return (GhaLogLineKind.Normal, ProcessAnsiColors(content));
    }

    private static string ProcessAnsiColors(string text)
    {
        if (!text.Contains('\x1B'))
            return WebUtility.HtmlEncode(text);

        var sb = new StringBuilder();
        bool inSpan = false;
        int lastIndex = 0;

        foreach (Match m in AnsiRx().Matches(text))
        {
            if (m.Index > lastIndex)
                sb.Append(WebUtility.HtmlEncode(text[lastIndex..m.Index]));

            var codeStr = m.Groups[1].Value;
            var code = 0;
            if (!string.IsNullOrEmpty(codeStr))
                _ = int.TryParse(codeStr.Split(';')[0], out code);

            if (code == 0)
            {
                if (inSpan) { sb.Append("</span>"); inSpan = false; }
            }
            else if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
            {
                if (inSpan) sb.Append("</span>");
                sb.Append($"<span class=\"ansi-{code}\">");
                inSpan = true;
            }

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < text.Length)
            sb.Append(WebUtility.HtmlEncode(text[lastIndex..]));
        if (inSpan)
            sb.Append("</span>");

        return sb.ToString();
    }
}
