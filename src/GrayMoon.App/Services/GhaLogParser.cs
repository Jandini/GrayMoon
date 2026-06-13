using System.Net;
using System.Text;

namespace GrayMoon.App.Services;

public enum GhaLogLineKind { Normal, Error, Warning, Command, Debug }

public abstract record GhaLogEntry;

public record GhaLogLineEntry(GhaLogLineKind Kind, string HtmlContent) : GhaLogEntry;

public record GhaLogGroupEntry(string Title, bool HasError, bool HasWarning, List<GhaLogLineEntry> Lines) : GhaLogEntry;

public static class GhaLogParser
{
    private static readonly string[] _ansiSpanTable = BuildAnsiSpanTable();

    private static string[] BuildAnsiSpanTable()
    {
        var t = new string[128];
        for (int c = 30; c <= 37; c++) t[c] = $"<span class=\"ansi-{c}\">";
        for (int c = 90; c <= 97; c++) t[c] = $"<span class=\"ansi-{c}\">";
        return t;
    }

    private sealed class GroupBuilder
    {
        public required string Title { get; init; }
        public bool HasError { get; set; }
        public bool HasWarning { get; set; }
        public List<GhaLogLineEntry> Lines { get; } = [];
    }

    // GitHub Actions log format:
    //   ##[group]Step Name    ← starts a new section; closes previous one
    //   metadata lines...     ← inside the "header" of the group
    //   ##[endgroup]          ← separates metadata from output; NOT a section closer
    //   actual output lines   ← still belong to the same section
    //   ##[group]Next step    ← THIS closes the previous section and starts the next
    //
    // So ##[endgroup] is simply ignored for structural purposes.
    // Everything from ##[group] to just before the next ##[group] (or EOF) = one collapsible unit.
    public static List<GhaLogEntry> ParseJobLog(string rawLog)
    {
        var entries = new List<GhaLogEntry>();
        if (string.IsNullOrEmpty(rawLog)) return entries;

        GroupBuilder? current = null;
        var sb = new StringBuilder(256);

        var remaining = rawLog.AsSpan();
        while (!remaining.IsEmpty)
        {
            int nl = remaining.IndexOf('\n');
            var raw = nl >= 0 ? remaining[..nl] : remaining;
            remaining = nl >= 0 ? remaining[(nl + 1)..] : default;

            var line = (!raw.IsEmpty && raw[^1] == '\r') ? raw[..^1] : raw;
            var content = StripTimestamp(line);

            if (content.StartsWith("##[group]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                    entries.Add(new GhaLogGroupEntry(current.Title, current.HasError, current.HasWarning, current.Lines));

                sb.Clear();
                AppendHtmlEncoded(content["##[group]".Length..], sb);
                current = new GroupBuilder { Title = sb.ToString() };
                continue;
            }

            if (content.TrimEnd().Equals("##[endgroup]".AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;

            var (kind, html) = ParseLineContent(content, sb);

            if (current != null)
            {
                current.Lines.Add(new GhaLogLineEntry(kind, html));
                if (kind == GhaLogLineKind.Error)   current.HasError   = true;
                if (kind == GhaLogLineKind.Warning) current.HasWarning = true;
            }
            else
            {
                entries.Add(new GhaLogLineEntry(kind, html));
            }
        }

        if (current != null)
            entries.Add(new GhaLogGroupEntry(current.Title, current.HasError, current.HasWarning, current.Lines));

        return entries;
    }

    private static ReadOnlySpan<char> StripTimestamp(ReadOnlySpan<char> line)
    {
        // Format: YYYY-MM-DDTHH:MM:SS.{fractional}Z  (minimum 23 chars)
        if (line.Length < 23 ||
            line[4] != '-' || line[7] != '-' || line[10] != 'T' ||
            line[13] != ':' || line[16] != ':' || line[19] != '.')
            return line;

        if (!IsDigit4(line, 0) || !IsDigit2(line, 5) || !IsDigit2(line, 8) ||
            !IsDigit2(line, 11) || !IsDigit2(line, 14) || !IsDigit2(line, 17))
            return line;

        int i = 20;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == 20) return line; // no fractional digits

        if (i + 1 >= line.Length || line[i] != 'Z' || line[i + 1] != ' ') return line;
        return line[(i + 2)..];
    }

    private static bool IsDigit4(ReadOnlySpan<char> s, int pos) =>
        char.IsAsciiDigit(s[pos]) && char.IsAsciiDigit(s[pos + 1]) &&
        char.IsAsciiDigit(s[pos + 2]) && char.IsAsciiDigit(s[pos + 3]);

    private static bool IsDigit2(ReadOnlySpan<char> s, int pos) =>
        char.IsAsciiDigit(s[pos]) && char.IsAsciiDigit(s[pos + 1]);

    private static (GhaLogLineKind Kind, string Html) ParseLineContent(ReadOnlySpan<char> content, StringBuilder sb)
    {
        // ##[...] prefix (older / GitHub internal format)
        if (content.StartsWith("##[error]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Error, ProcessAnsiColors(content["##[error]".Length..], sb));
        if (content.StartsWith("##[warning]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Warning, ProcessAnsiColors(content["##[warning]".Length..], sb));
        if (content.StartsWith("##[command]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Command, ProcessAnsiColors(content["##[command]".Length..], sb));
        if (content.StartsWith("##[debug]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Debug, ProcessAnsiColors(content["##[debug]".Length..], sb));

        // [...] prefix (format GitHub Actions runner uses for run: steps)
        if (content.StartsWith("[error]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Error, ProcessAnsiColors(content["[error]".Length..], sb));
        if (content.StartsWith("[warning]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Warning, ProcessAnsiColors(content["[warning]".Length..], sb));
        if (content.StartsWith("[command]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Command, ProcessAnsiColors(content["[command]".Length..], sb));
        if (content.StartsWith("[debug]".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return (GhaLogLineKind.Debug, ProcessAnsiColors(content["[debug]".Length..], sb));

        return (GhaLogLineKind.Normal, ProcessAnsiColors(content, sb));
    }

    private static string ProcessAnsiColors(ReadOnlySpan<char> text, StringBuilder sb)
    {
        sb.Clear();

        if (text.IndexOf('\x1B') < 0)
        {
            AppendHtmlEncoded(text, sb);
            return sb.ToString();
        }

        bool inSpan = false;
        int start = 0, i = 0;

        while (i < text.Length)
        {
            if (text[i] != '\x1B' || i + 1 >= text.Length || text[i + 1] != '[')
            {
                i++;
                continue;
            }

            int seqStart = i;
            int j = i + 2;
            while (j < text.Length && (text[j] == ';' || char.IsAsciiDigit(text[j]))) j++;

            // Not a valid SGR sequence — treat the ESC as literal and advance past it
            if (j >= text.Length || text[j] != 'm')
            {
                i++;
                continue;
            }

            if (seqStart > start) AppendHtmlEncoded(text[start..seqStart], sb);

            // Extract first semicolon-delimited code only (matches original behavior)
            int paramStart = seqStart + 2;
            int code = 0;
            if (j > paramStart)
            {
                int semi = text[paramStart..j].IndexOf(';');
                var firstCode = semi >= 0 ? text[paramStart..(paramStart + semi)] : text[paramStart..j];
                int.TryParse(firstCode, out code);
            }

            if (code == 0)
            {
                if (inSpan) { sb.Append("</span>"); inSpan = false; }
            }
            else if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
            {
                if (inSpan) sb.Append("</span>");
                sb.Append(_ansiSpanTable[code]);
                inSpan = true;
            }
            // All other codes silently consumed — matches original behavior

            start = j + 1;
            i = start;
        }

        if (start < text.Length) AppendHtmlEncoded(text[start..], sb);
        if (inSpan) sb.Append("</span>");

        return sb.ToString();
    }

    private static void AppendHtmlEncoded(ReadOnlySpan<char> text, StringBuilder sb)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string? entity = c switch
            {
                '&'  => "&amp;",
                '<'  => "&lt;",
                '>'  => "&gt;",
                '"'  => "&quot;",
                '\'' => "&#39;",
                _    => null
            };

            bool isSafe = c >= 0x20 && c <= 0x7E && entity == null;
            if (isSafe) continue;

            if (i > start) sb.Append(text[start..i]);

            if (entity != null)
            {
                sb.Append(entity);
                start = i + 1;
                continue;
            }

            // Non-ASCII / control char: find the full unsafe run and delegate to WebUtility
            int runEnd = i + 1;
            while (runEnd < text.Length)
            {
                char n = text[runEnd];
                if (n >= 0x20 && n <= 0x7E && n != '&' && n != '<' && n != '>' && n != '"' && n != '\'') break;
                runEnd++;
            }
            sb.Append(WebUtility.HtmlEncode(text[i..runEnd].ToString()));
            i = runEnd - 1;
            start = runEnd;
        }

        if (start < text.Length) sb.Append(text[start..]);
    }
}
