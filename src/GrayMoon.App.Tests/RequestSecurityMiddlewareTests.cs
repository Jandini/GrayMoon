using GrayMoon.App.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>F3: cross-site and rebinding protection for the REST API and the SignalR hubs.</summary>
public class RequestSecurityMiddlewareTests
{
    private const string NoForwardedHost = "";
    private static readonly string[] NoAllowedOrigins = [];

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

    // --- REST rule -------------------------------------------------------------

    [Fact]
    public void RestRequest_cross_site_origin_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://evil.example.com",
            requestHeaderValue: "1",
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.False(allowed);
    }

    [Fact]
    public void RestRequest_same_origin_with_header_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://localhost:8384",
            requestHeaderValue: "1",
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_same_origin_without_header_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://localhost:8384",
            requestHeaderValue: "",
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.False(allowed);
    }

    [Fact]
    public void RestRequest_loopback_origin_against_loopback_host_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://127.0.0.1:5000",
            requestHeaderValue: "1",
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_forwarded_host_match_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://graymoon.example.com",
            requestHeaderValue: "1",
            requestHost: "127.0.0.1",
            forwardedHost: "graymoon.example.com:443",
            allowedOrigins: NoAllowedOrigins);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_configured_allowed_origin_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://graymoon.example.com",
            requestHeaderValue: "1",
            requestHost: "127.0.0.1",
            forwardedHost: NoForwardedHost,
            allowedOrigins: ["graymoon.example.com"]);

        Assert.True(allowed);
    }

    [Fact]
    public void RestRequest_malformed_origin_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "not-a-uri",
            requestHeaderValue: "1",
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.False(allowed);
    }

    // --- Hub rule ----------------------------------------------------------------

    [Fact]
    public void HubOrigin_any_origin_on_worker_hub_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://localhost:8384",
            isWorkerHub: true,
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.False(allowed);
    }

    [Fact]
    public void HubOrigin_own_host_on_other_hub_is_allowed()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://localhost:8384",
            isWorkerHub: false,
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.True(allowed);
    }

    [Fact]
    public void HubOrigin_foreign_host_on_other_hub_is_blocked()
    {
        var allowed = RequestSecurityMiddleware.IsHubOriginAllowed(
            origin: "http://evil.example.com",
            isWorkerHub: false,
            requestHost: "localhost",
            forwardedHost: NoForwardedHost,
            allowedOrigins: NoAllowedOrigins);

        Assert.False(allowed);
    }

    // --- Full middleware pipeline --------------------------------------------------

    private sealed class Flag { public bool Called; }

    private static (RequestSecurityMiddleware Middleware, Flag Flag) CreateMiddlewareWithFlag(SecurityOptions? options = null)
    {
        var flag = new Flag();
        RequestDelegate next = _ =>
        {
            flag.Called = true;
            return Task.CompletedTask;
        };
        return (new RequestSecurityMiddleware(next, Options.Create(options ?? new SecurityOptions())), flag);
    }

    [Fact]
    public async Task Middleware_GET_on_api_path_is_unaffected_even_with_foreign_origin()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        context.Request.Method = "GET";
        context.Request.Headers.Origin = "http://evil.example.com";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_with_no_origin_passes()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        context.Request.Method = "POST";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_cross_site_origin_is_403()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        context.Request.Method = "POST";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://evil.example.com";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_POST_on_api_path_same_origin_with_header_passes()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        context.Request.Method = "POST";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://localhost";
        context.Request.Headers["X-GrayMoon-Request"] = "1";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Middleware_hub_worker_with_any_origin_is_403(string path)
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://localhost";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Middleware_hub_worker_without_origin_connects(string path)
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hubs_workspace_sync_foreign_origin_is_403()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/workspace-sync";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://evil.example.com";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_hubs_workspace_sync_own_origin_connects()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/workspace-sync";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://localhost";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_blazor_hub_without_origin_connects()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";
        context.Request.Method = "GET";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_desktop_hub_foreign_origin_is_403()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/desktop";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://evil.example.com";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_desktop_hub_own_origin_connects()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/desktop";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://localhost";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_blazor_hub_foreign_origin_is_403()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://evil.example.com";

        await middleware.InvokeAsync(context);

        Assert.False(flag.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_blazor_hub_own_origin_connects()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://localhost";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hub_127_0_0_1_origin_against_localhost_host_connects()
    {
        // R-F3d: the Desktop WebView and a tab opened via 127.0.0.1 must both work regardless of
        // which loopback name the App itself is bound to.
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/workspace-sync";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "http://127.0.0.1:8384";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hub_forwarded_host_match_connects()
    {
        // Docker behind a reverse proxy: the Origin matches X-Forwarded-Host, not the raw Host header.
        var (middleware, flag) = CreateMiddlewareWithFlag();
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/workspace-sync";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("127.0.0.1");
        context.Request.Headers.Origin = "https://graymoon.example.com";
        context.Request.Headers["X-Forwarded-Host"] = "graymoon.example.com";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }

    [Fact]
    public async Task Middleware_hub_configured_allowed_origin_connects()
    {
        var (middleware, flag) = CreateMiddlewareWithFlag(new SecurityOptions { AllowedOrigins = ["graymoon.example.com"] });
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/workspace-sync";
        context.Request.Method = "GET";
        context.Request.Host = new HostString("127.0.0.1");
        context.Request.Headers.Origin = "https://graymoon.example.com";

        await middleware.InvokeAsync(context);

        Assert.True(flag.Called);
    }
}
