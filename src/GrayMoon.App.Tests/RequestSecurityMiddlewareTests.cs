using GrayMoon.App.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>F3: cross-site and rebinding protection for the REST API and the SignalR hubs.</summary>
public class RequestSecurityMiddlewareTests
{
    private static readonly string[] NoAllowedHosts = [];

    // --- Path classification -------------------------------------------------

    [Theory]
    [InlineData("/api/workspaces")]
    [InlineData("/API/Workspaces")]
    [InlineData("/repos/1/connector")]
    public void IsRestPath_matches_api_and_repos(string path)
        => Assert.True(RequestSecurityMiddleware.IsRestPath(path));

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    [InlineData("/hubs/workspace-sync")]
    [InlineData("/_blazor")]
    public void IsRestPath_does_not_match_hub_paths(string path)
        => Assert.False(RequestSecurityMiddleware.IsRestPath(path));

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public void IsHubPath_flags_worker_hub_specifically(string path)
    {
        Assert.True(RequestSecurityMiddleware.IsHubPath(path, out var isWorkerHub));
        Assert.True(isWorkerHub);
    }

    [Theory]
    [InlineData("/hubs/workspace-sync")]
    [InlineData("/hubs/desktop")]
    [InlineData("/_blazor")]
    public void IsHubPath_matches_other_hubs_without_worker_flag(string path)
    {
        Assert.True(RequestSecurityMiddleware.IsHubPath(path, out var isWorkerHub));
        Assert.False(isWorkerHub);
    }

    [Fact]
    public void IsHubPath_does_not_match_rest_paths()
    {
        Assert.False(RequestSecurityMiddleware.IsHubPath("/api/workspaces", out var isWorkerHub));
        Assert.False(isWorkerHub);
    }

    // --- Allowlist ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData(" ; * ; ")]
    public void AllowedHosts_wildcard_or_empty_setting_is_an_empty_allowlist(string? setting)
        => Assert.Empty(RequestSecurityMiddleware.BuildAllowedHosts(setting, []));

    [Fact]
    public void AllowedHosts_merges_the_setting_and_the_security_origins()
    {
        var hosts = RequestSecurityMiddleware.BuildAllowedHosts("localhost; nas.local ;192.168.1.10", ["graymoon.example.com", "*"]);

        Assert.Equal(4, hosts.Count);
        Assert.Contains("nas.local", hosts);
        Assert.Contains("192.168.1.10", hosts);
        Assert.Contains("graymoon.example.com", hosts);
    }

    // --- REST rule -------------------------------------------------------------

    [Fact]
    public void RestRequest_cross_site_origin_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://evil.example.com",
            requestHeaderValue: "1",
            allowedHosts: NoAllowedHosts);

        Assert.False(allowed);
    }

    [Fact]
    public void RestRequest_loopback_origin_with_header_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://localhost:8384",
            requestHeaderValue: "1",
            allowedHosts: NoAllowedHosts);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_loopback_origin_without_header_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://localhost:8384",
            requestHeaderValue: "",
            allowedHosts: NoAllowedHosts);

        Assert.False(allowed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:8384")]
    [InlineData("http://LOCALHOST")]
    public void RestRequest_any_loopback_origin_is_allowed_with_an_empty_allowlist(string origin)
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: origin,
            requestHeaderValue: "1",
            allowedHosts: NoAllowedHosts);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_listed_host_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://graymoon.example.com",
            requestHeaderValue: "1",
            allowedHosts: ["graymoon.example.com"]);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_wildcard_subdomain_entry_matches_subdomains_only()
    {
        string[] hosts = ["*.example.com"];

        Assert.True(RequestSecurityMiddleware.IsRestRequestAllowed("http://graymoon.example.com", "1", hosts));
        Assert.False(RequestSecurityMiddleware.IsRestRequestAllowed("http://example.com", "1", hosts));
        Assert.False(RequestSecurityMiddleware.IsRestRequestAllowed("http://evilexample.com", "1", hosts));
    }

    [Fact]
    public void RestRequest_malformed_origin_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "not-a-uri",
            requestHeaderValue: "1",
            allowedHosts: NoAllowedHosts);

        Assert.False(allowed);
    }

    // --- Hub rule ----------------------------------------------------------------

    [Fact]
    public void HubOrigin_any_origin_on_worker_hub_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://localhost:8384",
            isWorkerHub: true,
            allowedHosts: NoAllowedHosts);

        Assert.False(allowed);
    }

    [Fact]
    public void HubOrigin_loopback_on_other_hub_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://localhost:8384",
            isWorkerHub: false,
            allowedHosts: NoAllowedHosts);

        Assert.True(allowed);
    }

    [Fact]
    public void HubOrigin_foreign_host_on_other_hub_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://evil.example.com",
            isWorkerHub: false,
            allowedHosts: NoAllowedHosts);

        Assert.False(allowed);
    }

    // --- Full middleware pipeline --------------------------------------------------

    private sealed class Flag { public bool Called; }

    private sealed class CountingLogger : ILogger<RequestSecurityMiddleware>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    /// <param name="allowedHosts">The <c>AllowedHosts</c> setting (what <c>GRAYMOON_AllowedHosts</c> sets);
    /// null means not configured, which is the Docker default.</param>
    private static (RequestSecurityMiddleware Middleware, Flag Flag, CountingLogger Logger) CreateMiddlewareWithFlag(
        SecurityOptions? options = null,
        string? allowedHosts = null)
    {
        var flag = new Flag();
        RequestDelegate next = _ =>
        {
            flag.Called = true;
            return Task.CompletedTask;
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts })
            .Build();
        var logger = new CountingLogger();
        return (new RequestSecurityMiddleware(next, Options.Create(options ?? new SecurityOptions()), configuration, logger), flag, logger);
    }

    private static DefaultHttpContext CreateContext(string path, string method, string? host = null, string? origin = null, bool withRequestHeader = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        if (host is not null)
            context.Request.Host = new HostString(host);
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        if (withRequestHeader)
            context.Request.Headers["X-GrayMoon-Request"] = "1";
        return context;
    }

    // Review 2026-11-10 finding 7: under DNS rebinding the hostile page's name is both the Origin host and
    // the Host header, and the page is same-origin so it can add any custom header. Equality with Host (or
    // with X-Forwarded-Host, which the page can also set) must not let the request through.
    [Theory]
    [InlineData("/api/workspaces", "POST")]
    [InlineData("/repos/1/branches", "POST")]
    [InlineData("/_blazor/negotiate", "POST")]
    [InlineData("/hubs/workspace-sync", "GET")]
    [InlineData("/hubs/desktop", "GET")]
    public async Task Middleware_rebinding_origin_equal_to_host_but_not_listed_is_403(string path, string method)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext(path, method, host: "rebind.evil.example.com:8384", origin: "http://rebind.evil.example.com:8384", withRequestHeader: true);
        context.Request.Headers["X-Forwarded-Host"] = "rebind.evil.example.com:8384";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_rebinding_origin_is_403_even_when_other_hosts_are_listed()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag(allowedHosts: "localhost;nas.local");
        var context = CreateContext("/api/workspaces", "POST", host: "rebind.evil.example.com", origin: "http://rebind.evil.example.com", withRequestHeader: true);

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/workspaces", "POST")]
    [InlineData("/_blazor/negotiate", "POST")]
    [InlineData("/hubs/workspace-sync", "GET")]
    public async Task Middleware_host_listed_in_AllowedHosts_passes(string path, string method)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag(allowedHosts: "localhost;nas.local;192.168.1.10");
        var context = CreateContext(path, method, host: "nas.local:8384", origin: "http://nas.local:8384", withRequestHeader: true);

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_wildcard_AllowedHosts_is_treated_as_empty_and_lan_origin_is_403()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag(allowedHosts: "*");
        var context = CreateContext("/api/workspaces", "POST", host: "nas.local:8384", origin: "http://nas.local:8384", withRequestHeader: true);

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void Empty_allowlist_logs_one_startup_warning_that_names_the_variable(string? allowedHosts)
    {
        var (_, _, logger) = CreateMiddlewareWithFlag(allowedHosts: allowedHosts);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("GRAYMOON_AllowedHosts", warning);
    }

    [Fact]
    public void Configured_allowlist_logs_no_warning()
    {
        var (_, _, hostsLogger) = CreateMiddlewareWithFlag(allowedHosts: "localhost;127.0.0.1;[::1]");
        var (_, _, originsLogger) = CreateMiddlewareWithFlag(new SecurityOptions { AllowedOrigins = ["graymoon.example.com"] });

        Assert.Empty(hostsLogger.Warnings);
        Assert.Empty(originsLogger.Warnings);
    }

    [Fact]
    public async Task Middleware_GET_on_api_path_is_unaffected_even_with_foreign_origin()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/api/workspaces", "GET", origin: "http://evil.example.com");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_with_no_origin_passes()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/api/workspaces", "POST");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_cross_site_origin_is_403()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/api/workspaces", "POST", host: "localhost", origin: "http://evil.example.com");

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_loopback_origin_with_header_passes()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/api/workspaces", "POST", host: "localhost", origin: "http://localhost", withRequestHeader: true);

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Middleware_hub_worker_with_any_origin_is_403(string path)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext(path, "GET", host: "localhost", origin: "http://localhost");

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Middleware_hub_worker_without_origin_connects(string path)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext(path, "GET");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Theory]
    [InlineData("/hubs/workspace-sync")]
    [InlineData("/hubs/desktop")]
    [InlineData("/_blazor")]
    public async Task Middleware_hub_foreign_origin_is_403(string path)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext(path, "GET", host: "localhost", origin: "http://evil.example.com");

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/hubs/workspace-sync")]
    [InlineData("/hubs/desktop")]
    [InlineData("/_blazor")]
    public async Task Middleware_hub_loopback_origin_connects(string path)
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext(path, "GET", host: "localhost", origin: "http://localhost");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_blazor_hub_without_origin_connects()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/_blazor", "GET");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hub_127_0_0_1_origin_against_localhost_host_connects()
    {
        // R-F3d: the Desktop WebView and a tab opened via 127.0.0.1 must both work regardless of
        // which loopback name the App itself is bound to. Desktop starts the App with exactly this allowlist.
        var (middleware, flag, _) = CreateMiddlewareWithFlag(allowedHosts: "localhost;127.0.0.1;[::1]");
        var context = CreateContext("/hubs/workspace-sync", "GET", host: "localhost", origin: "http://127.0.0.1:8384");

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hub_forwarded_host_alone_is_not_proof()
    {
        // Docker behind a reverse proxy: X-Forwarded-Host is a request header the page can influence, so the
        // public host name has to be listed (GRAYMOON_AllowedHosts or Security:AllowedOrigins).
        var (middleware, flag, _) = CreateMiddlewareWithFlag();
        var context = CreateContext("/hubs/workspace-sync", "GET", host: "127.0.0.1", origin: "https://graymoon.example.com");
        context.Request.Headers["X-Forwarded-Host"] = "graymoon.example.com";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_hub_configured_allowed_origin_connects()
    {
        var (middleware, flag, _) = CreateMiddlewareWithFlag(new SecurityOptions { AllowedOrigins = ["graymoon.example.com"] });
        var context = CreateContext("/hubs/workspace-sync", "GET", host: "127.0.0.1", origin: "https://graymoon.example.com");
        context.Request.Headers["X-Forwarded-Host"] = "graymoon.example.com";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }
}
