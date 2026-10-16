using System.Text.RegularExpressions;

namespace GrayMoon.Common.Ai;

/// <summary>
/// Conservative redaction of secret-looking text in evidence before it leaves the machine. A line that looks like it
/// assigns a password, token or key, or that contains a well-known token format, is replaced as a whole: losing one line
/// of context is cheap, leaking a credential to a provider is not.
/// </summary>
public static partial class EvidenceRedactor
{
    public const string Placeholder = "[redacted: possible secret]";

    // A secret-named key (a whole word, so cancellationToken or GetToken() never match) given a literal value: either a
    // quoted string (code, JSON, YAML) or the bare value of a config/env line (KEY=value, key: value).
    [GeneratedRegex(
        @"(?i)\b(password|passwd|pwd|secret|client[_-]?secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|access[_-]?key|private[_-]?key)\b[""']?\s*[:=]\s*(""[^""\s]{6,}""|'[^'\s]{6,}')|(?im)^[+\- ]?\s*[A-Z0-9_.-]*(password|passwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key)[A-Z0-9_.-]*\s*[:=]\s*[^\s""'(){};,]{8,}\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    // Well-known credential formats: GitHub, OpenAI/Anthropic style keys, AWS access keys, Slack, JWT, private key blocks,
    // and connection strings carrying a password.
    [GeneratedRegex(
        @"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?i:password\s*=\s*[^;\s]+;))",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownToken();

    /// <summary>Text with every secret-looking line replaced. Diff markers (<c>+</c>, <c>-</c>, space) are kept.</summary>
    public static string Redact(string text, out int redactedLines)
    {
        redactedLines = 0;
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!LooksSecret(line))
                continue;

            var marker = line.Length > 0 && line[0] is '+' or '-' or ' ' ? line[0].ToString() : string.Empty;
            lines[i] = marker + Placeholder;
            redactedLines++;
        }

        return redactedLines == 0 ? text : string.Join('\n', lines);
    }

    public static bool LooksSecret(string line) =>
        line.Length > 0 && (KnownToken().IsMatch(line) || Assignment().IsMatch(line));
}
