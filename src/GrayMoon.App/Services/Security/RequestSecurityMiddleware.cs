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
///
/// An Origin is accepted only when its host is a loopback name or is listed in the host allowlist
/// (<c>AllowedHosts</c>, normally set with the <c>GRAYMOON_AllowedHosts</c> environment variable, plus
/// <c>Security:AllowedOrigins</c>). An Origin whose host merely equals the request's <c>Host</c> or
/// <c>X-Forwarded-Host</c> header proves nothing: under DNS rebinding the hostile page's own name arrives
/// in both. With an empty allowlist only loopback is accepted and one warning is logged at startup.
///
/// The request's own <c>Host</c> is held to the same list, for every path and method. A page reached through
/// a rebound name is same-origin with GrayMoon, so its GET requests carry no <c>Origin</c> and the Origin rules
/// above never see them; the <c>Host</c> header still names the hostile host, and that is what is refused here.
/// Exempt are <c>/health</c> and the Worker paths, which <see cref="WorkerSecretMiddleware"/> authenticates
/// with the Worker secret, and a request with no <c>Host</c> at all, which no browser sends.
/// </summary>
public sealed class RequestSecurityMiddleware
{
    private const string RequestHeaderName = "X-GrayMoon-Request";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyCollection<string> _allowedHosts;
    private readonly ILogger<RequestSecurityMiddleware> _logger;
    private int _hostRejectionLogged;

    /// <summary>The plain-text body of a request refused for its <c>Host</c>. It names the fix and never echoes the header.</summary>
    internal const string HostRejectedMessage =
        "GrayMoon does not accept requests for this host name. Open GrayMoon as http://localhost:<port>, or set " +
        "GRAYMOON_AllowedHosts to the host names or addresses people type in the browser, separated by ';'.";

    // Explicit constructor: it builds the allowlist once and logs the empty-allowlist warning once.
    public RequestSecurityMiddleware(
        RequestDelegate next,
        IOptions<SecurityOptions> options,
        IConfiguration configuration,
        ILogger<RequestSecurityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowedHosts = BuildAllowedHosts(configuration["AllowedHosts"], options.Value.AllowedOrigins);
        if (_allowedHosts.Count == 0)
        {
            logger.LogWarning(
                "No allowed hosts are configured, so GrayMoon accepts browser requests only from this machine (localhost). " +
                "To use GrayMoon from another machine, set GRAYMOON_AllowedHosts to the host names or addresses people type " +
                "in the browser, separated by ';' (for example GRAYMOON_AllowedHosts=localhost;nas.local;192.168.1.10).");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var origin = context.Request.Headers.Origin.ToString();

        var requestHost = context.Request.Host.Host;
        if (!IsRequestHostAllowed(requestHost, path, _allowedHosts))
        {
            // Logged once: a browser on a host that is not listed repeats the request for every asset.
            if (Interlocked.Exchange(ref _hostRejectionLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Rejected a request for host '{Host}': it is not this machine (localhost) and is not listed in GRAYMOON_AllowedHosts. " +
                    "Further rejections are not logged.", requestHost);
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(HostRejectedMessage);
            return;
        }

        if (IsHubPath(path, out var isWorkerHub))
        {
            if (!string.IsNullOrEmpty(origin) &&
                !IsHubOriginAllowed(origin, isWorkerHub, _allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (IsRestPath(path) && !HttpMethods.IsGet(context.Request.Method))
        {
            if (!string.IsNullOrEmpty(origin) &&
                !IsRestRequestAllowed(origin, context.Request.Headers[RequestHeaderName].ToString(), _allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await _next(context);
    }

    /// <summary>The host allowlist: the entries of the <c>AllowedHosts</c> setting (';' separated, the
    /// same value ASP.NET Core host filtering reads; set by <c>GRAYMOON_AllowedHosts</c>) plus
    /// <c>Security:AllowedOrigins</c>. A bare <c>*</c> is not a host and is dropped, so "allow all" can
    /// never be configured here; <c>*.example.com</c> matches any subdomain.</summary>
    internal static IReadOnlyCollection<string> BuildAllowedHosts(string? allowedHostsSetting, IEnumerable<string>? allowedOrigins)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (allowedHostsSetting ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddHost(hosts, entry);

        foreach (var entry in allowedOrigins ?? [])
            AddHost(hosts, entry?.Trim() ?? string.Empty);

        return hosts;
    }

    private static void AddHost(HashSet<string> hosts, string entry)
    {
        if (entry.Length == 0 || entry == "*")
            return;

        hosts.Add(entry);
    }

    /// <summary>True when the request's own <c>Host</c> is acceptable: a loopback name, a listed host, or no
    /// host at all (no browser sends that). <c>/health</c> and the Worker paths are not checked here: a
    /// container probe and a Worker on another machine address the App by names nobody lists, and the Worker
    /// paths are authenticated by <see cref="WorkerSecretMiddleware"/>.</summary>
    internal static bool IsRequestHostAllowed(string requestHost, string path, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrEmpty(requestHost))
            return true;

        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            WorkerSecretMiddleware.Classify(path) != WorkerSecretMiddleware.ProtectedPath.None)
            return true;

        return IsAllowedHost(requestHost, allowedHosts);
    }

    /// <summary>True when a non-GET <c>/api/</c> or <c>/repos/</c> request carrying an <c>Origin</c> header
    /// is allowed: the Origin's host must be a loopback name or a listed host, and the request must carry
    /// <c>X-GrayMoon-Request: 1</c>, which a cross-site page cannot add without triggering a CORS preflight.
    /// Call only when <paramref name="origin"/> is non-empty; a request without an Origin header always
    /// passes and never calls this method.</summary>
    internal static bool IsRestRequestAllowed(string origin, string requestHeaderValue, IReadOnlyCollection<string> allowedHosts)
    {
        if (!TryGetOriginHost(origin, out var originHost))
            return false;

        if (!IsAllowedHost(originHost, allowedHosts))
            return false;

        return requestHeaderValue == "1";
    }

    /// <summary>True when a hub connection (negotiate or WebSocket upgrade) carrying an <c>Origin</c> header
    /// is allowed. <c>/hub/worker</c> rejects every <c>Origin</c> header outright (no legitimate Worker ever
    /// sends one); the other hubs (<c>/hubs/workspace-sync</c>, <c>/hubs/desktop</c>, <c>/_blazor</c>) accept
    /// an Origin whose host is a loopback name or a listed host. Call only when <paramref name="origin"/> is
    /// non-empty; a request without an Origin header always passes and never calls this method.</summary>
    internal static bool IsHubOriginAllowed(string origin, bool isWorkerHub, IReadOnlyCollection<string> allowedHosts)
    {
        if (isWorkerHub)
            return false;

        if (!TryGetOriginHost(origin, out var originHost))
            return false;

        return IsAllowedHost(originHost, allowedHosts);
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

    /// <summary>True for a loopback name or a listed host; used for an Origin's host and for the request's Host.</summary>
    private static bool IsAllowedHost(string originHost, IReadOnlyCollection<string> allowedHosts)
    {
        if (IsLoopbackHost(originHost))
            return true;

        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(originHost, allowed, StringComparison.OrdinalIgnoreCase))
                return true;

            // "*.example.com" (the host filtering wildcard form) matches any subdomain, not the bare domain.
            if (allowed.StartsWith("*.", StringComparison.Ordinal) && allowed.Length > 2 &&
                originHost.Length > allowed.Length - 1 &&
                originHost.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase))
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
