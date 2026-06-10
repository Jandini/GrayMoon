using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>
/// Emits one summary line per GitHub HTTP request into the overlay terminal.
/// Reads only headers, status, elapsed time, and a short (max 80 char) preview of textual
/// response bodies. Never reads request bodies or any auth header. Append failures are
/// swallowed so the HTTP pipeline is never affected by terminal issues.
/// </summary>
internal sealed partial class GitHubOverlayLoggingHandler(OverlayCommandTerminalService overlayTerminal)
    : DelegatingHandler
{
    private const string Label = "github";
    private const int MaxBodyChars = 80;
    private const long MaxBufferBytes = 16 * 1024;

    private static readonly string[] SensitiveQueryKeys =
    [
        "token", "secret", "key", "auth", "password", "code"
    ];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)(token|secret|password|authorization|access_token|refresh_token)""?\s*[:=]\s*""?[^"",}\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValueRegex();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var method = request.Method.Method;
        var path = SafePath(request.RequestUri);

        TryAppend(AgentCommandStreamKind.Stdout, $"-> {method} {path}");

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();

            var responseKind = response.IsSuccessStatusCode
                ? AgentCommandStreamKind.CommandLine
                : AgentCommandStreamKind.Stderr;

            // Append the status line immediately - this fires Changed so Blazor gets a render slot
            // before the caller's continuation (which often closes the overlay) runs.
            TryAppend(responseKind, $"<- {(int)response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

            // Body preview is a separate, lower-priority line. Awaiting here yields the circuit so
            // the status line above has a chance to render before the overlay is dismissed.
            var bodyPreview = await TryReadBodyPreviewAsync(response, cancellationToken);
            if (!string.IsNullOrEmpty(bodyPreview))
                TryAppend(AgentCommandStreamKind.CommandLine, $"   {bodyPreview}");

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            TryAppend(
                AgentCommandStreamKind.Stderr,
                $"<- FAILED {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
    }

    private static async Task<string?> TryReadBodyPreviewAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = response.Content;
            if (content is null)
                return null;

            if (!IsTextualContent(content.Headers.ContentType?.MediaType))
                return null;

            // Skip preview for responses whose size is unknown or exceeds the buffer limit.
            // Attempting LoadIntoBufferAsync on a stream larger than MaxBufferBytes partially
            // consumes the network stream, corrupting the response body for the actual caller.
            var contentLength = content.Headers.ContentLength;
            if (contentLength is null or > MaxBufferBytes)
                return null;

            // Buffer the response so the original caller can still consume it normally.
            await content.LoadIntoBufferAsync(MaxBufferBytes);

            var raw = await content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var collapsed = WhitespaceRegex().Replace(raw, " ").Trim();
            var scrubbed = SensitiveValueRegex().Replace(collapsed, "$1=<redacted>");

            if (scrubbed.Length > MaxBodyChars)
                scrubbed = scrubbed[..MaxBodyChars] + "...";

            return scrubbed;
        }
        catch
        {
            // Body too large, decoding failed, or content already disposed - just skip the preview.
            return null;
        }
    }

    private static bool IsTextualContent(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase))
        {
            return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void TryAppend(AgentCommandStreamKind kind, string text)
    {
        try
        {
            overlayTerminal.Append(Label, kind, text);
        }
        catch
        {
            // Never let terminal failures break the HTTP pipeline.
        }
    }

    private static string SafePath(Uri? uri)
    {
        if (uri is null)
            return "(no-uri)";

        var absolute = uri.IsAbsoluteUri ? uri : new Uri(new Uri("https://api.github.com/"), uri);
        var path = absolute.AbsolutePath;
        var query = absolute.Query;

        if (string.IsNullOrEmpty(query) || query == "?")
            return path;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();

        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;

            if (IsSensitiveKey(key))
                continue;

            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(pair);
        }

        return builder.Length == 0 ? path : path + "?" + builder;
    }

    private static bool IsSensitiveKey(string key)
    {
        foreach (var sensitive in SensitiveQueryKeys)
        {
            if (key.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
