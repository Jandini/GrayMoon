using System.Net;
using System.Text;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class ConnectorProGetTests
{
    private const string Server = "https://proget.company.com/";
    private const string Endpoint = "https://proget.company.com/nuget/team-feed/v3/index.json";
    private const string HealthUrl = "https://proget.company.com/health?format=json";
    private const string FeedListUrl = "https://proget.company.com/api/management/feeds/list";
    private const string Health = """{"applicationName":"ProGet","databaseStatus":"OK","licenseStatus":"OK","versionNumber":"2025.10"}""";
    private const string NuGetIndex = """{"version":"3.0.0","resources":[{"@id":"https://proget.company.com/nuget/team-feed/v3/flatcontainer/","@type":"PackageBaseAddress/3.0.0"}]}""";
    private const string ApiKey = "secret-key";
    private static readonly string BasicApiKey = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + ApiKey));

    // ---- Address ----------------------------------------------------------------------------

    [Theory]
    [InlineData("https://proget.company.com/nuget/team-feed/v3/index.json")]
    [InlineData("https://proget.company.com/nuget/team-feed/")]
    [InlineData("https://proget.company.com/nuget/team-feed/package/Acme.Lib/1.2.3")]
    [InlineData("https://proget.company.com/feeds/team-feed/Acme.Lib/1.2.3")]
    [InlineData("proget.company.com/nuget/team-feed")]
    public void A_feed_address_gives_the_server_and_the_feed(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, input);

        Assert.True(resolution.IsValid);
        Assert.Equal(ConnectorEndpointKind.ProGet, resolution.Kind);
        Assert.Equal(Server, resolution.ServerUrl);
        Assert.Equal("team-feed", resolution.Feed);
        Assert.Equal([Endpoint], resolution.Candidates);
    }

    [Fact]
    public void A_server_address_alone_has_no_feed_and_no_endpoint_yet()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, "https://proget.company.com");

        Assert.True(resolution.IsValid);
        Assert.Equal(Server, resolution.ServerUrl);
        Assert.Null(resolution.Feed);
        Assert.Empty(resolution.Candidates);
    }

    [Fact]
    public void The_feed_box_completes_a_server_address_and_a_feed_in_the_address_wins_over_it()
    {
        var fromBox = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, "https://proget.company.com", " team-feed ");
        var fromAddress = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, Endpoint, "other-feed");

        Assert.Equal([Endpoint], fromBox.Candidates);
        Assert.Equal("team-feed", fromAddress.Feed);
    }

    [Fact]
    public void A_feed_name_with_a_slash_is_refused()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, "https://proget.company.com", "team/feed");

        Assert.False(resolution.IsValid);
        Assert.Equal(ConnectorUrlResolver.FeedInvalidMessage, resolution.Error);
        Assert.Equal(Server, resolution.ServerUrl);
    }

    [Fact]
    public void A_server_under_a_base_path_and_a_port_keeps_both()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, "http://tools.company.com:8624/proget/nuget/team-feed/v3/index.json");

        Assert.Equal("http://tools.company.com:8624/proget/", resolution.ServerUrl);
        Assert.Equal(["http://tools.company.com:8624/proget/nuget/team-feed/v3/index.json"], resolution.Candidates);
    }

    [Fact]
    public void A_saved_endpoint_is_shown_as_server_and_feed_and_the_name_follows_the_feed()
    {
        var form = new ConnectorForm { ConnectorType = ConnectorType.ProGet, ApiBaseUrl = Endpoint };

        ConnectorFormAutoFill.SplitProGetAddress(form);

        Assert.Equal(Server, form.ApiBaseUrl);
        Assert.Equal("team-feed", form.FeedName);
        Assert.Equal("ProGet", ConnectorUrlResolver.DefaultName(ConnectorType.ProGet));
        Assert.Equal("ProGet team-feed", ConnectorUrlResolver.DefaultName(ConnectorType.ProGet, form.FeedName));
    }

    [Fact]
    public void Leaving_proget_drops_the_feed_name_and_no_token_page_is_offered()
    {
        var form = new ConnectorForm { ConnectorType = ConnectorType.NuGet, ApiBaseUrl = Endpoint, FeedName = "team-feed" };

        ConnectorFormAutoFill.ApplyTypeChange(form, ConnectorType.ProGet);

        Assert.Null(form.FeedName);
        Assert.Equal(Endpoint, form.ApiBaseUrl);
        Assert.False(ConnectorUrlResolver.OffersTokenPage(ConnectorType.ProGet, Server));
    }

    [Fact]
    public void Switching_an_existing_nuget_connector_to_proget_keeps_its_address_and_key_and_drops_the_hidden_user_name()
    {
        var form = new ConnectorForm
        {
            ConnectorType = ConnectorType.ProGet,
            ConnectorName = "Company feed",
            ApiBaseUrl = Endpoint,
            UserName = "someone",
            UserToken = ApiKey
        };

        ConnectorFormAutoFill.ApplyTypeChange(form, ConnectorType.NuGet);

        Assert.Equal(Endpoint, form.ApiBaseUrl);
        Assert.Equal(ApiKey, form.UserToken);
        Assert.Null(form.UserName);
        // Update then saves the endpoint read from that address.
        Assert.Equal([Endpoint], ConnectorUrlResolver.Resolve(ConnectorType.ProGet, form.ApiBaseUrl, form.FeedName).Candidates);
    }

    [Fact]
    public void A_proget_connector_needs_no_user_name_and_its_key_is_optional()
    {
        Assert.False(ConnectorHelpers.RequiresToken(ConnectorType.ProGet, Endpoint));
        Assert.False(ConnectorHelpers.ShouldShowUserNameField(ConnectorType.ProGet, Endpoint));
        Assert.True(ConnectorHelpers.ShouldShowTokenField(ConnectorType.ProGet, Endpoint));
        Assert.True(ConnectorHelpers.IsPackageRegistry(ConnectorType.ProGet));
        Assert.False(ConnectorHelpers.IsPackageRegistry(ConnectorType.GitHub));
    }

    // ---- Test button ------------------------------------------------------------------------

    [Fact]
    public async Task A_feed_address_is_checked_with_the_key_as_basic_sign_in_for_the_user_api()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.RespondByAuthorization(Endpoint, authorization => authorization == BasicApiKey
            ? (HttpStatusCode.OK, NuGetIndex)
            : (HttpStatusCode.Unauthorized, ""));

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, Endpoint, null, ApiKey);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal(Endpoint, result.ResolvedUrl);
        Assert.Equal(Server, result.ServerUrl);
        Assert.Equal("team-feed", result.Feed);
        Assert.Equal("ProGet 2025.10 feed team-feed found - connection OK.", result.Message);
    }

    [Fact]
    public async Task A_public_feed_is_found_without_a_key()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond(Endpoint, HttpStatusCode.OK, NuGetIndex);

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, "https://proget.company.com", null, null, feedName: "team-feed");

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal(Endpoint, result.ResolvedUrl);
        Assert.DoesNotContain(handler.Requests, r => r.Authorization != null || r.ApiKeyHeader != null);
    }

    [Fact]
    public async Task A_protected_feed_without_a_key_asks_for_one()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond(Endpoint, HttpStatusCode.Unauthorized, "");

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, Endpoint, null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsToken, result.Outcome);
        Assert.Equal(Endpoint, result.ResolvedUrl);
        Assert.Equal("ProGet 2025.10 feed team-feed found - enter an API key to check access.", result.Message);
    }

    [Fact]
    public async Task A_rejected_key_keeps_the_feed_and_says_so()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond(Endpoint, HttpStatusCode.Unauthorized, "");

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, Endpoint, null, ApiKey);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.True(result.EndpointFound);
        Assert.Equal("The API key was rejected for feed team-feed.", result.Message);
    }

    [Fact]
    public async Task A_server_address_alone_finds_the_server_and_asks_for_the_feed()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, "https://proget.company.com", null, null);

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsFeed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Equal(Server, result.ServerUrl);
        Assert.Equal("ProGet 2025.10 found - enter the feed name.", result.Message);
        Assert.Equal([HealthUrl], handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task A_key_that_may_list_feeds_offers_the_nuget_feeds_to_choose_from()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond(FeedListUrl, HttpStatusCode.OK,
            """[{"name":"team-feed","feedType":"nuget"},{"name":"npm-feed","feedType":"npm"},{"name":"approved","feedType":"NuGet"}]""");

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, "https://proget.company.com", null, ApiKey);

        Assert.Equal(ConnectorEndpointTestOutcome.NeedsFeed, result.Outcome);
        Assert.Equal(["approved", "team-feed"], result.Feeds);
        Assert.Equal("ProGet 2025.10 found - choose a feed.", result.Message);
        Assert.Equal(ApiKey, handler.Requests.Single(r => r.Url == FeedListUrl).ApiKeyHeader);
    }

    [Fact]
    public async Task The_only_nuget_feed_of_a_server_is_taken_without_asking()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond(FeedListUrl, HttpStatusCode.OK, """[{"name":"team-feed","feedType":"nuget"},{"name":"npm-feed","feedType":"npm"}]""");
        handler.Respond(Endpoint, HttpStatusCode.OK, NuGetIndex);

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, "https://proget.company.com", null, ApiKey);

        Assert.Equal(ConnectorEndpointTestOutcome.Ok, result.Outcome);
        Assert.Equal(Endpoint, result.ResolvedUrl);
        Assert.Equal("team-feed", result.Feed);
    }

    [Fact]
    public async Task A_feed_that_is_not_on_the_server_fails_on_the_feed_and_keeps_the_server()
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, HttpStatusCode.OK, Health);
        handler.Respond("https://proget.company.com/nuget/wrong/v3/index.json", HttpStatusCode.NotFound, "");

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, "https://proget.company.com", null, null, feedName: "wrong");

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Equal(Server, result.ServerUrl);
        Assert.Equal("No NuGet feed named wrong on this server.", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"applicationName":"BuildMaster"}""")]
    [InlineData(HttpStatusCode.OK, "<html>Welcome</html>")]
    [InlineData(HttpStatusCode.NotFound, "")]
    public async Task The_key_is_never_sent_to_a_server_that_has_not_answered_as_proget(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler();
        handler.Respond(HealthUrl, status, body);
        handler.Respond(Endpoint, HttpStatusCode.Unauthorized, "");

        var result = await Tester(handler).TestAsync(ConnectorType.ProGet, Endpoint, null, ApiKey);

        Assert.Equal(ConnectorEndpointTestOutcome.Failed, result.Outcome);
        Assert.False(result.EndpointFound);
        Assert.Null(result.ServerUrl);
        Assert.StartsWith("Could not find a ProGet server at this address", result.Message);
        Assert.Equal([HealthUrl], handler.Requests.Select(r => r.Url));
    }

    // ---- Package lookups --------------------------------------------------------------------

    [Fact]
    public async Task Package_lookups_of_a_proget_connector_sign_in_as_api_with_the_key()
    {
        var handler = new ScriptedHandler();
        handler.RespondByAuthorization(Endpoint, authorization => authorization == BasicApiKey
            ? (HttpStatusCode.OK, NuGetIndex)
            : (HttpStatusCode.Unauthorized, ""));
        handler.RespondByAuthorization("https://proget.company.com/nuget/team-feed/v3/flatcontainer/acme.lib/index.json", authorization => authorization == BasicApiKey
            ? (HttpStatusCode.OK, """{"versions":["1.2.3"]}""")
            : (HttpStatusCode.Unauthorized, ""));
        var service = new NuGetService(new HttpClient(handler), NullLogger<NuGetService>.Instance);
        var connector = new Connector
        {
            ConnectorName = "ProGet team-feed",
            ConnectorType = ConnectorType.ProGet,
            ApiBaseUrl = Endpoint,
            UserToken = ConnectorHelpers.ProtectToken(ApiKey),
            IsActive = true
        };

        Assert.True(await service.PackageVersionExistsAsync(connector, "Acme.Lib", "1.2.3"));
        Assert.True((await service.TestConnectionAsync(connector)).Success);
        // Every request carried the ProGet sign-in; the old code sent a Bearer header only when the URL said "proget".
        Assert.All(handler.Requests, r => Assert.Equal(BasicApiKey, r.Authorization));
    }

    [Fact]
    public async Task A_public_proget_feed_tests_ok_without_a_key()
    {
        var handler = new ScriptedHandler();
        handler.Respond(Endpoint, HttpStatusCode.OK, NuGetIndex);
        var service = new NuGetService(new HttpClient(handler), NullLogger<NuGetService>.Instance);
        var connector = new Connector { ConnectorName = "ProGet team-feed", ConnectorType = ConnectorType.ProGet, ApiBaseUrl = Endpoint, IsActive = true };

        var result = await service.TestConnectionAsync(connector);

        Assert.True(result.Success);
    }

    // ---- Harness ----------------------------------------------------------------------------

    private static ConnectorEndpointTester Tester(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<ConnectorEndpointTester>.Instance);

    private sealed record SentRequest(string Url, string? Authorization, string? ApiKeyHeader);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string?, (HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests => _requests;

        public void Respond(string url, HttpStatusCode status, string body) => _routes[url] = _ => (status, body);

        public void RespondByAuthorization(string url, Func<string?, (HttpStatusCode Status, string Body)> respond) => _routes[url] = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var authorization = request.Headers.Authorization?.ToString();
            var apiKey = request.Headers.TryGetValues("X-ApiKey", out var values) ? values.FirstOrDefault() : null;
            _requests.Add(new SentRequest(url, authorization, apiKey));

            var (status, body) = _routes.TryGetValue(url, out var route) ? route(authorization) : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
