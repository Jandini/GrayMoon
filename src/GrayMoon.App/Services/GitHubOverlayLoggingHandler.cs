using System.Diagnostics;
using System.Text;
using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>
/// Emits one summary line per GitHub HTTP request into the overlay terminal.
/// Never reads request/response bodies or headers; only method, path (with scrubbed query),
/// status code, and elapsed time. Append failures are swallowed so the HTTP pipeline is never
/// affected by terminal issues.
/// </summary>
internal sealed class GitHubOverlayLoggingHandler(OverlayCommandTerminalService overlayTerminal)
    : DelegatingHandler
{
    private const string Label = "github";

    private static readonly string[] SensitiveQueryKeys =
    [
        "token", "secret", "key", "auth", "password", "code"
    ];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var method = request.Method.Method;
        var path = SafePath(request.RequestUri);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();

            var kind = response.IsSuccessStatusCode
                ? AgentCommandStreamKind.Stdout
                : AgentCommandStreamKind.Stderr;

            TryAppend(kind, $"{method} {path} {(int)response.StatusCode} ({sw.ElapsedMilliseconds}ms)");
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
                $"{method} {path} FAILED {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
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
