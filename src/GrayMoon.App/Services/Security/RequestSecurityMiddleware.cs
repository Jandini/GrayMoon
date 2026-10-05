using GrayMoon.App.Hubs;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Security;

/// <summary>
/// Blocks cross-site and rebinding requests against the REST API and the SignalR hubs (F3).
///
/// GrayMoon has no user login, so the only thing stopping another page open in the same browser from
/// triggering a push, a sync, or anything else is this middleware: browsers always send <c>Origin</c> on a
/// cross-site POST and on every hub connection attempt, but never add a custom header to one without a CORS
/// preflight, which GrayMoon does not allow. A request with no <c>Origin</c> header at all (a script,
/// <c>curl</c>, the Worker's .NET SignalR client, the App's own server-side hub clients) is never a browser
/// page and always passes unchanged.
/// </summary>
public sealed class RequestSecurityMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    private const string RequestHeaderName = "X-GrayMoon-Request";
    private const string ForwardedHostHeaderName = "X-Forwarded-Host";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var origin = context.Request.Headers.Origin.ToString();

        if (IsHubPath(path, out var isWorkerHub))
        {
            if (!string.IsNullOrEmpty(origin) &&
                !IsHubOriginAllowed(origin, isWorkerHub, context.Request.Host.Host, context.Request.Headers[ForwardedHostHeaderName].ToString(), options.Value.AllowedOrigins))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (IsRestPath(path) && !HttpMethods.IsGet(context.Request.Method))
        {
            if (!string.IsNullOrEmpty(origin) &&
                !IsRestRequestAllowed(origin, context.Request.Headers[RequestHeaderName].ToString(), context.Request.Host.Host, context.Request.Headers[ForwardedHostHeaderName].ToString(), options.Value.AllowedOrigins))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next(context);
    }

    /// <summary>True when a non-GET <c>/api/</c> or <c>/repos/</c> request carrying an <c>Origin</c> header
    /// is allowed: the Origin's host must be this app's own host (or a loopback name, or a configured
    /// allowed origin) and the request must carry <c>X-GrayMoon-Request: 1</c>, which a cross-site page
    /// cannot add without triggering a CORS preflight. Call only when <paramref name="origin"/> is non-empty;
    /// a request without an Origin header always passes and never calls this method.</summary>
    internal static bool IsRestRequestAllowed(string origin, string requestHeaderValue, string requestHost, string forwardedHost, IReadOnlyCollection<string> allowedOrigins)
    {
        if (!TryGetOriginHost(origin, out var originHost))
            return false;

        if (!IsAllowedOriginHost(originHost, requestHost, forwardedHost, allowedOrigins))
            return false;

        return requestHeaderValue == "1";
    }

    /// <summary>True when a hub connection (negotiate or WebSocket upgrade) carrying an <c>Origin</c> header
    /// is allowed. <c>/hub/worker</c> rejects every <c>Origin</c> header outright (no legitimate Worker ever
    /// sends one); the other hubs (<c>/hubs/workspace-sync</c>, <c>/hubs/desktop</c>, <c>/_blazor</c>) accept
    /// an Origin whose host matches this app's own host. Call only when <paramref name="origin"/> is
    /// non-empty; a request without an Origin header always passes and never calls this method.</summary>
    internal static bool IsHubOriginAllowed(string origin, bool isWorkerHub, string requestHost, string forwardedHost, IReadOnlyCollection<string> allowedOrigins)
    {
        if (isWorkerHub)
            return false;

        if (!TryGetOriginHost(origin, out var originHost))
            return false;

        return IsAllowedOriginHost(originHost, requestHost, forwardedHost, allowedOrigins);
    }

    internal static bool IsRestPath(string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/repos/", StringComparison.OrdinalIgnoreCase);

    /// <param name="isWorkerHub">True when <paramref name="path"/> is under <c>/hub/worker</c> specifically,
    /// which has its own, stricter rule (any Origin is rejected).</param>
    internal static bool IsHubPath(string path, out bool isWorkerHub)
    {
        isWorkerHub = WorkerHubRoutes.IsWorkerHubPath(path);
        return isWorkerHub ||
            path.StartsWith("/hubs/workspace-sync", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hubs/desktop", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetOriginHost(string origin, out string host)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            host = originUri.Host;
            return true;
        }

        host = string.Empty;
        return false;
    }

    private static bool IsAllowedOriginHost(string originHost, string requestHost, string forwardedHost, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.Equals(originHost, requestHost, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(forwardedHost))
        {
            // X-Forwarded-Host may carry a port; compare the host part only.
            var forwardedHostOnly = forwardedHost.Split(':')[0];
            if (string.Equals(originHost, forwardedHostOnly, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (IsLoopbackHost(originHost))
            return true;

        foreach (var allowed in allowedOrigins)
        {
            if (string.Equals(originHost, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host == "127.0.0.1" ||
        host == "::1" ||
        host == "[::1]" ||
        host == "0.0.0.0";
}
