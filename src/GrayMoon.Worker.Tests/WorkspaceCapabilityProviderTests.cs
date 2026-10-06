using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The git hooks are the one direction of traffic with no app request to carry capabilities on, so the
/// Worker resolves them itself: a cache every command warms, then the app's API, then full enrichment.
/// </summary>
public sealed class WorkspaceCapabilityProviderTests
{
    private const string Secret = "worker-secret-value";

    private static WorkspaceCapabilityProvider Create(string? appApiBaseUrl, string? secret = Secret)
        => new(
            Options.Create(new WorkerOptions { AppApiBaseUrl = appApiBaseUrl }),
            new FakeWorkerSecretProvider(secret),
            NullLogger<WorkspaceCapabilityProvider>.Instance);

    [Fact]
    public async Task Cold_cache_resolves_capabilities_from_the_app_endpoint()
    {
        using var app = new FakeAppApi();
        app.RespondWith(calculateVersion: false, discoverProjects: false);
        var provider = Create(app.BaseUrl);

        var capabilities = await provider.GetAsync(42);

        Assert.False(capabilities.ShouldCalculateVersion);
        Assert.False(capabilities.ShouldDiscoverProjects);

        var request = Assert.Single(app.Requests);
        Assert.Contains("GET /workspaces/42/capabilities ", request, StringComparison.Ordinal);
        Assert.Contains($"{WorkerSecretHeader.Name}: {Secret}", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_resolved_answer_is_cached_so_the_next_hook_makes_no_call()
    {
        using var app = new FakeAppApi();
        app.RespondWith(calculateVersion: true, discoverProjects: false);
        var provider = Create(app.BaseUrl);

        await provider.GetAsync(42);
        var second = await provider.GetAsync(42);

        Assert.True(second.ShouldCalculateVersion);
        Assert.False(second.ShouldDiscoverProjects);
        Assert.Single(app.Requests);
    }

    [Fact]
    public async Task A_command_warmed_cache_serves_a_hook_with_no_http_call()
    {
        using var app = new FakeAppApi();
        var provider = Create(app.BaseUrl);

        // What every app-initiated command does on its way through the dispatcher.
        provider.Remember(42, RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        var capabilities = await provider.GetAsync(42);

        Assert.False(capabilities.ShouldCalculateVersion);
        Assert.False(capabilities.ShouldDiscoverProjects);
        Assert.Empty(app.Requests);
    }

    [Fact]
    public async Task An_unreachable_app_falls_back_to_full_enrichment_without_caching_the_guess()
    {
        using var app = new FakeAppApi();
        app.StatusCode = 503;
        var provider = Create(app.BaseUrl);

        var fallback = await provider.GetAsync(42);

        // Losing a known version because the app was briefly down is worse than one wasted probe.
        Assert.True(fallback.ShouldCalculateVersion);
        Assert.True(fallback.ShouldDiscoverProjects);

        // A fallback is a guess, so it must not become the answer for the rest of the process's life.
        app.RespondWith(calculateVersion: false, discoverProjects: false);
        var afterRecovery = await provider.GetAsync(42);

        Assert.False(afterRecovery.ShouldCalculateVersion);
        Assert.Equal(2, app.Requests.Count);
    }

    [Fact]
    public async Task A_refused_connection_falls_back_to_full_enrichment()
    {
        var provider = Create($"http://127.0.0.1:{FakeAppApi.FindClosedPort()}");

        var capabilities = await provider.GetAsync(42);

        Assert.True(capabilities.ShouldCalculateVersion);
        Assert.True(capabilities.ShouldDiscoverProjects);
    }

    [Fact]
    public async Task An_unconfigured_app_api_falls_back_to_full_enrichment()
    {
        var provider = Create(appApiBaseUrl: null);

        var capabilities = await provider.GetAsync(42);

        Assert.True(capabilities.ShouldCalculateVersion);
        Assert.True(capabilities.ShouldDiscoverProjects);
    }

    [Fact]
    public async Task Invalidation_makes_the_next_resolution_refetch()
    {
        using var app = new FakeAppApi();
        app.RespondWith(calculateVersion: true, discoverProjects: true);
        var provider = Create(app.BaseUrl);

        await provider.GetAsync(42);
        app.RespondWith(calculateVersion: false, discoverProjects: false);
        provider.Invalidate(42);

        var capabilities = await provider.GetAsync(42);

        Assert.False(capabilities.ShouldCalculateVersion);
        Assert.Equal(2, app.Requests.Count);
    }

    [Fact]
    public async Task A_worker_installed_before_secrets_existed_still_asks()
    {
        using var app = new FakeAppApi();
        app.RespondWith(calculateVersion: false, discoverProjects: true);
        var provider = Create(app.BaseUrl, secret: null);

        var capabilities = await provider.GetAsync(7);

        Assert.False(capabilities.ShouldCalculateVersion);
        Assert.True(capabilities.ShouldDiscoverProjects);
        Assert.DoesNotContain(WorkerSecretHeader.Name, Assert.Single(app.Requests), StringComparison.OrdinalIgnoreCase);
    }
}
