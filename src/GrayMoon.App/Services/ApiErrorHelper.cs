namespace GrayMoon.App.Services;

/// <summary>Extracts user-facing error messages from API response bodies (JSON or plain text).</summary>
public static class ApiErrorHelper
{
    /// <summary>Tries to extract a user-facing error message from an API response body.</summary>
    public static string? TryGetErrorMessageFromResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorMessage", out var em) && em.ValueKind == System.Text.Json.JsonValueKind.String)
                return em.GetString();
            if (root.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                return d.GetString();
            if (root.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                return t.GetString();
        }
        catch
        {
            // Not JSON or invalid
        }
        return trimmed.Length > 500 ? trimmed[..500] + "…" : trimmed;
    }
}
