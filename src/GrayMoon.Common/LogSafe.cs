namespace GrayMoon.Common;

/// <summary>
/// Replaces tokens and credentials in strings so they are safe to log. Uses simple string scanning; no regex.
/// </summary>
public static class LogSafe
{
    private const string Replacement = "***";

    /// <summary>
    /// Returns a copy of <paramref name="text"/> with bearer tokens and URL credentials replaced by ***.
    /// Safe to call on null or empty; does minimal work when no secrets are present.
    /// </summary>
    public static string ForLog(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var s = text;
        if (s.IndexOf("http.extraHeader=", StringComparison.OrdinalIgnoreCase) >= 0)
            s = RedactHttpExtraHeader(s);
        if (s.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0)
            s = RedactUrlCredentials(s);
        return s;
    }

    private static string RedactHttpExtraHeader(string s)
    {
        var extraHeaderStart = s.IndexOf("http.extraHeader=", StringComparison.OrdinalIgnoreCase);
        while (extraHeaderStart >= 0)
        {
            var valueStart = extraHeaderStart + "http.extraHeader=".Length;
            if (valueStart <= s.Length && s[valueStart - 1] == '=')
            {
                var quote = valueStart < s.Length && s[valueStart] == '"';
                var start = quote ? valueStart + 1 : valueStart;
                var end = start;
                if (quote)
                {
                    while (end < s.Length && s[end] != '"')
                    {
                        if (s[end] == '\\' && end + 1 < s.Length) end++;
                        end++;
                    }
                    if (end < s.Length) end++;
                }
                else
                {
                    while (end < s.Length && s[end] != ' ' && s[end] != '\t') end++;
                }
                s = s.Substring(0, start) + Replacement + (quote ? "\"" : "") + (end < s.Length ? s.Substring(end) : "");
            }
            extraHeaderStart = s.IndexOf("http.extraHeader=", extraHeaderStart + 1, StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    private static string RedactUrlCredentials(string s)
    {
        foreach (var scheme in new[] { "https://", "http://" })
        {
            var i = 0;
            while (true)
            {
                i = s.IndexOf(scheme, i, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                var afterScheme = i + scheme.Length;
                var at = s.IndexOf('@', afterScheme);
                if (at > afterScheme)
                {
                    s = s.Substring(0, afterScheme) + Replacement + "@" + s.Substring(at + 1);
                    i = afterScheme + Replacement.Length + 1;
                }
                else
                    i = afterScheme;
            }
        }
        return s;
    }
}
