using System.Net;
using System.Text;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class ConnectorEndpointTesterTests
{
    private const string GitHubRoot = """{"current_user_url":"https://api.github.com/user"}""";
    private const string NuGetIndex = """{"version":"3.0.0","resources":[]}""";
    private const string Token = "secret-token";

    // ---- GitHub ------------------------------------------------------------------------------

    [Fact]
    public async Task A_repository_address_is_replaced_by_the_api_and_the_token_is_checked_there()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://api.github.com/", HttpStatusCode.OK, GitHubRoot);
        handler.Respond("https://api.github.com/user", HttpStatusCode.OK, """{"login":"me"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://github.com/acme/repo", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://api.github.com/", result.ResolvedUrl);
        Assert.Equal("GitHub API found - connection OK.", result.Message);
        Assert.Equal(["https://api.github.com/", "https://api.github.com/user"], handler.Urls);
    }

    [Fact]
    public async Task Without_a_token_the_endpoint_is_found_and_the_user_is_asked_for_one()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://api.github.com/", HttpStatusCode.OK, GitHubRoot);

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "github.com/acme/repo", null, "  ");

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsToken, result.Outcome);
        Assert.Equal("https://api.github.com/", result.ResolvedUrl);
        Assert.Equal("GitHub API found - enter a token to check sign-in.", result.Message);
        Assert.Equal(["https://api.github.com/"], handler.Urls);
    }

    [Fact]
    public async Task A_company_host_is_found_under_api_v3_even_when_it_refuses_anonymous_calls()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://git.company.com/api/v3/",
            HttpStatusCode.Unauthorized,
            """{"message":"Must authenticate to access this API.","documentation_url":"https://docs.github.com/enterprise-server@3.17/rest"}""",
            ("X-GitHub-Enterprise-Version", "3.17.2"));
        handler.Respond("https://git.company.com/api/v3/user", HttpStatusCode.OK, """{"login":"me"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://git.company.com/team/repo", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://git.company.com/api/v3/", result.ResolvedUrl);
        Assert.Equal("GitHub Enterprise Server 3.17.2 API found - connection OK.", result.Message);
    }

    [Fact]
    public async Task The_token_is_never_sent_to_a_host_that_has_not_answered_as_github()
    {
        var handler = new ScriptedHandler();
        // The first candidate answers, but not as a GitHub API; the second one is the real endpoint.
        handler.Respond("https://git.company.com/api/v3/", HttpStatusCode.OK, "<html>Sign in</html>");
        handler.Respond("https://api.git.company.com/", HttpStatusCode.OK, GitHubRoot);
        handler.Respond("https://api.git.company.com/user", HttpStatusCode.OK, """{"login":"me"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://git.company.com/team/repo", null, Token);

        Assert.Equal("https://api.git.company.com/", result.ResolvedUrl);
        var withToken = handler.Requests.Where(r => r.Authorization != null).Select(r => r.Url).ToList();
        Assert.Equal(["https://api.git.company.com/user"], withToken);
    }

    [Fact]
    public async Task A_rejected_token_keeps_the_found_endpoint_and_says_so()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://api.github.com/", HttpStatusCode.OK, GitHubRoot);
        handler.Respond("https://api.github.com/user", HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://github.com/acme/repo", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.True(result.EndpointFound);
        Assert.Equal("GitHub API found, but the token was rejected.", result.Message);
    }

    [Fact]
    public async Task An_address_with_no_github_behind_it_fails_without_an_endpoint_and_names_the_last_error()
    {
        var handler = new ScriptedHandler();
        handler.Throw("https://git.company.com/api/v3/", new HttpRequestException("No such host is known."));
        handler.Throw("https://api.git.company.com/", new HttpRequestException("No such host is known."));
        handler.Respond("https://git.company.com/team/repo/", HttpStatusCode.NotFound, "<html></html>");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://git.company.com/team/repo", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Equal("Could not reach a GitHub API at this address (HTTP 404).", result.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Authorization != null);
    }

    [Fact]
    public async Task An_exact_address_is_tested_but_not_rewritten()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://ghe.example.com/api/v3/", HttpStatusCode.OK, GitHubRoot);
        handler.Respond("https://ghe.example.com/api/v3/user", HttpStatusCode.OK, """{"login":"me"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "https://ghe.example.com/api/v3", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://ghe.example.com/api/v3", result.ResolvedUrl);
    }

    [Fact]
    public async Task Text_that_is_not_an_address_fails_without_any_request()
    {
        var handler = new ScriptedHandler();

        var result = await Tester(handler).TestAsync(ConnectorType.GitHub, "not a url", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.Equal(ConnectorUrlResolver.InvalidMessage, result.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- GitHub Packages ---------------------------------------------------------------------

    [Fact]
    public async Task A_repository_address_becomes_the_owners_feed_and_is_checked_with_the_token()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://nuget.pkg.github.com/acme/index.json", HttpStatusCode.OK, NuGetIndex);

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://github.com/acme/repo", "me", Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://nuget.pkg.github.com/acme/index.json", result.ResolvedUrl);
        Assert.Equal("acme", result.Owner);
        Assert.Equal("GitHub Packages feed for acme - connection OK.", result.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:" + Token)), request.Authorization);
    }

    [Fact]
    public async Task A_feed_without_an_owner_is_refused_before_any_request()
    {
        var handler = new ScriptedHandler();

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://nuget.pkg.github.com/", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Equal(ConnectorUrlResolver.OwnerRequiredMessage, result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_packages_feed_without_a_token_names_the_owner_and_asks_for_one()
    {
        var handler = new ScriptedHandler();

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://github.com/acme", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsToken, result.Outcome);
        Assert.Equal("https://nuget.pkg.github.com/acme/index.json", result.ResolvedUrl);
        Assert.Equal("GitHub Packages feed for acme - enter a token to check access.", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false, "No packages feed found for acme. Check the organization or user name.")]
    [InlineData(HttpStatusCode.Unauthorized, true, "The token was rejected for acme. It needs the read:packages scope.")]
    [InlineData(HttpStatusCode.Forbidden, true, "The token was rejected for acme. It needs the read:packages scope.")]
    public async Task A_packages_feed_failure_says_whether_the_owner_or_the_token_is_wrong(HttpStatusCode status, bool endpointFound, string message)
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://nuget.pkg.github.com/acme/index.json", status, "{}");

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://nuget.pkg.github.com/acme/", null, Token);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.Equal(endpointFound, result.EndpointFound);
        Assert.Equal(message, result.Message);
    }

    // ---- Other NuGet feeds -------------------------------------------------------------------

    [Fact]
    public async Task Nuget_org_is_found_without_a_token()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://api.nuget.org/v3/index.json", HttpStatusCode.OK, NuGetIndex);

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "nuget.org", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://api.nuget.org/v3/index.json", result.ResolvedUrl);
        Assert.Equal("NuGet.org feed found - connection OK.", result.Message);
    }

    [Fact]
    public async Task Another_feed_falls_back_to_the_plain_index_when_the_v3_index_is_missing()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://feeds.example/nuget/team/v3/index.json", HttpStatusCode.NotFound, "{}");
        handler.Respond("https://feeds.example/nuget/team/index.json", HttpStatusCode.OK, NuGetIndex);

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://feeds.example/nuget/team", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://feeds.example/nuget/team/index.json", result.ResolvedUrl);
    }

    [Fact]
    public async Task A_protected_feed_without_a_token_asks_for_one()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://feeds.example/nuget/team/v3/index.json", HttpStatusCode.Unauthorized, "");
        handler.Respond("https://feeds.example/nuget/team/index.json", HttpStatusCode.Unauthorized, "");

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://feeds.example/nuget/team", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsToken, result.Outcome);
        Assert.Equal("https://feeds.example/nuget/team/v3/index.json", result.ResolvedUrl);
        Assert.Equal("This feed needs a token - enter one and click Test again.", result.Message);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_nuget_index_is_not_a_feed()
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://feeds.example/v3/index.json", HttpStatusCode.OK, "<html>Welcome</html>");
        handler.Respond("https://feeds.example/index.json", HttpStatusCode.OK, """{"hello":"world"}""");

        var result = await Tester(handler).TestAsync(ConnectorType.NuGet, "https://feeds.example", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Equal("Could not find a NuGet feed at this address (HTTP 200).", result.Message);
    }

    [Fact]
    public async Task Cancelling_the_test_is_not_reported_as_an_unreachable_endpoint()
    {
        var handler = new ScriptedHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tester(handler).TestAsync(ConnectorType.GitHub, "https://github.com/acme/repo", null, Token, cts.Token));
    }

    // ---- Harness -----------------------------------------------------------------------------

    private static ConnectorEndpointTester Tester(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<ConnectorEndpointTester>.Instance);

    private sealed record SentRequest(string Url, string? Authorization);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests => _requests;

        public IReadOnlyList<string> Urls => _requests.Select(r => r.Url).ToList();

        public void Respond(string url, HttpStatusCode status, string body, params (string Name, string Value)[] headers)
            => _routes[url] = () =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                foreach (var (name, value) in headers)
                    response.Headers.TryAddWithoutValidation(name, value);
                return response;
            };

        public void Throw(string url, Exception exception) => _routes[url] = () => throw exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Add(new SentRequest(url, request.Headers.Authorization?.ToString()));
            return Task.FromResult(_routes.TryGetValue(url, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }
}
