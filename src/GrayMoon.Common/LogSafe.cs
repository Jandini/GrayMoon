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
        const string cQuote = "-c \"";
        var i = 0;
        while (true)
        {
            var cStart = s.IndexOf(cQuote, i, StringComparison.Ordinal);
            if (cStart < 0) break;
            var contentStart = cStart + cQuote.Length;
            var contentEnd = contentStart;
            while (contentEnd < s.Length && s[contentEnd] != '"')
            {
                if (s[contentEnd] == '\\' && contentEnd + 1 < s.Length) contentEnd++;
                contentEnd++;
            }
            if (contentEnd < s.Length)
            {
                var content = s.Substring(contentStart, contentEnd - contentStart);
                if (content.StartsWith("http.extraHeader=", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(0, contentStart) + Replacement + s.Substring(contentEnd);
                i = contentEnd + 1;
            }
            else
                break;
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
