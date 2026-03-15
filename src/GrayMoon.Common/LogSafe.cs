namespace GrayMoon.Common;

/// <summary>
/// Redacts git http.extraHeader credentials in strings so they are safe to log.
/// </summary>
public static class LogSafe
{
    private const string Replacement = "***";

    /// <summary>
    /// Returns a copy of <paramref name="text"/> with git http.extraHeader credentials replaced by ***.
    /// Safe to call on null or empty; does minimal work when no secrets are present.
    /// </summary>
    public static string ForLog(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var s = text;
        if (s.IndexOf("http.extraHeader=", StringComparison.OrdinalIgnoreCase) >= 0)
            s = RedactHttpExtraHeader(s);
        return s;
    }

    private static string RedactHttpExtraHeader(string s)
    {
        const string prefix = "http.extraHeader=";
        var i = 0;
        while (i <= s.Length)
        {
            var start = s.IndexOf(prefix, i, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var valueStart = start + prefix.Length;
            if (valueStart >= s.Length) break;
            var end = s.IndexOf('"', valueStart);
            if (end < 0) break;
            s = s.Substring(0, valueStart) + Replacement + s.Substring(end);
            i = valueStart + Replacement.Length;
        }
        return s;
    }
}
