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

    private sealed class GroupBuilder
    {
        public required string Title { get; init; }
        public bool HasError { get; set; }
        public List<GhaLogLineEntry> Lines { get; } = [];
    }

    public static List<GhaLogEntry> ParseJobLog(string rawLog)
    {
        var entries = new List<GhaLogEntry>();
        if (string.IsNullOrEmpty(rawLog)) return entries;

        var stack = new Stack<GroupBuilder>();

        foreach (var raw in rawLog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var content = TimestampRx().Replace(line, "", 1);

            if (content.TrimEnd().Equals("##[endgroup]", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count > 0)
                {
                    var g = stack.Pop();
                    if (stack.Count == 0)
                    {
                        entries.Add(new GhaLogGroupEntry(g.Title, g.HasError, g.Lines));
                    }
                    else
                    {
                        // Nested group: flatten into parent with a sub-header line
                        var parent = stack.Peek();
                        parent.Lines.Add(new GhaLogLineEntry(GhaLogLineKind.Normal,
                            $"<span class=\"gha-sub-group-header\">▸ {g.Title}</span>"));
                        parent.Lines.AddRange(g.Lines);
                        if (g.HasError) parent.HasError = true;
                    }
                }
                continue;
            }

            if (content.StartsWith("##[group]", StringComparison.OrdinalIgnoreCase))
            {
                stack.Push(new GroupBuilder { Title = WebUtility.HtmlEncode(content["##[group]".Length..]) });
                continue;
            }

            var (kind, html) = ParseLineContent(content);

            if (stack.Count > 0)
            {
                var top = stack.Peek();
                top.Lines.Add(new GhaLogLineEntry(kind, html));
                if (kind == GhaLogLineKind.Error) top.HasError = true;
            }
            else
            {
                entries.Add(new GhaLogLineEntry(kind, html));
            }
        }

        // Flush any unclosed groups (top of stack = innermost)
        while (stack.Count > 0)
        {
            var g = stack.Pop();
            entries.Insert(0, new GhaLogGroupEntry(g.Title, g.HasError, g.Lines));
        }

        return entries;
    }

    private static (GhaLogLineKind Kind, string Html) ParseLineContent(string content)
    {
        // Detect markers with ##[...] prefix (older GitHub format)
        if (content.StartsWith("##[error]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Error, ProcessAnsiColors(content["##[error]".Length..]));
        if (content.StartsWith("##[warning]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Warning, ProcessAnsiColors(content["##[warning]".Length..]));
        if (content.StartsWith("##[command]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Command, ProcessAnsiColors(content["##[command]".Length..]));
        if (content.StartsWith("##[debug]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Debug, ProcessAnsiColors(content["##[debug]".Length..]));

        // Detect markers with [...]  prefix (format used by GitHub Actions runner for run: steps)
        if (content.StartsWith("[error]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Error, ProcessAnsiColors(content["[error]".Length..]));
        if (content.StartsWith("[warning]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Warning, ProcessAnsiColors(content["[warning]".Length..]));
        if (content.StartsWith("[command]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Command, ProcessAnsiColors(content["[command]".Length..]));
        if (content.StartsWith("[debug]", StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Debug, ProcessAnsiColors(content["[debug]".Length..]));

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
