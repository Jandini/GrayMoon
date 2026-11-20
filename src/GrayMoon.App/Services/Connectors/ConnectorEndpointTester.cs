using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Connectors;

public enum ConnectorEndpointTestOutcome
{
    /// <summary>The endpoint answered and, where a token is needed, the token was accepted.</summary>
    Ok,

    /// <summary>The endpoint was found; a token is needed to check access.</summary>
    NeedsToken,

    /// <summary>ProGet: the server was found; the feed still has to be named.</summary>
    NeedsFeed,

    /// <summary>No endpoint was found, or it refused the token.</summary>
    Failed
}

/// <summary>
/// Outcome of the Test button in the connector dialog. <see cref="ResolvedUrl"/> is the address to save and is null
/// when no endpoint was found. <see cref="Owner"/> is the GitHub Packages owner when the address names one.
/// </summary>
public sealed record ConnectorEndpointTestResult(
    ConnectorEndpointTestOutcome Outcome,
    string Message,
    string? ResolvedUrl = null,
    string? Owner = null)
{
    public bool EndpointFound => ResolvedUrl != null;

    /// <summary>ProGet only: the server address once the server has answered as ProGet, also when the feed was not found.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>ProGet only: the feed the result is about.</summary>
    public string? Feed { get; init; }

    /// <summary>ProGet only: the server's NuGet feeds, when the API key may list them. Empty otherwise.</summary>
    public IReadOnlyList<string> Feeds { get; init; } = [];
}

/// <summary>The feeds for the Feed dropdown; <see cref="Message"/> says why the list is empty.</summary>
public sealed record ProGetFeedListResult(IReadOnlyList<string> Feeds, string? Message = null);

/// <summary>
/// Finds the API endpoint behind what the user typed in the connector dialog and checks it.
/// Host policy (deliberate): the address is fetched whatever host it names, private networks included, because an
/// on-premises GitHub or package feed lives there and the saved connector calls that same host anyway. In return
/// only http(s) is fetched, each answer is read up to <see cref="MaxBodyBytes"/>, each request has a timeout, and a
/// GitHub token is sent only to an endpoint that has already identified itself as a GitHub API without it, and a
/// ProGet API key only to a server whose health page has identified it as ProGet.
/// </summary>
public sealed class ConnectorEndpointTester(HttpClient httpClient, ILogger<ConnectorEndpointTester> logger)
{
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<ConnectorEndpointTestResult> TestAsync(
        ConnectorType connectorType,
        string? input,
        string? userName,
        string? token,
        CancellationToken cancellationToken = default,
        string? feedName = null)
    {
        var resolution = ConnectorUrlResolver.Resolve(connectorType, input, feedName);
        if (!resolution.IsValid)
            return new(ConnectorEndpointTestOutcome.Failed, resolution.Error!);

        var typed = input!.Trim();
        var plainToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        return resolution.Kind switch
        {
            ConnectorEndpointKind.GitHubPackages => await TestGitHubPackagesAsync(resolution, typed, userName, plainToken, cancellationToken),
            ConnectorEndpointKind.NuGetOrg or ConnectorEndpointKind.NuGetFeed => await TestNuGetFeedAsync(resolution, typed, plainToken, cancellationToken),
            ConnectorEndpointKind.ProGet => await TestProGetAsync(resolution, userName, plainToken, cancellationToken),
            _ => await TestGitHubAsync(resolution, typed, plainToken, cancellationToken)
        };
    }

    private async Task<ConnectorEndpointTestResult> TestGitHubAsync(
        ConnectorUrlResolution resolution, string typed, string? token, CancellationToken cancellationToken)
    {
        string? lastError = null;
        foreach (var candidate in resolution.Candidates)
        {
            // No token here: the host has not shown that it is a GitHub API yet.
            var probe = await SendAsync(candidate, null, cancellationToken);
            if (!LooksLikeGitHubApi(probe))
            {
                lastError = probe.Describe();
                continue;
            }

            var label = GitHubLabel(resolution.Kind, probe.EnterpriseVersion);
            var address = ConnectorUrlResolver.AddressToSave(resolution, typed, candidate);
            if (token == null)
                return new(ConnectorEndpointTestOutcome.NeedsToken, $"{label} API found - enter a token to check sign-in.", address);

            var signIn = await SendAsync(candidate + "user", new AuthenticationHeaderValue("Bearer", token), cancellationToken);
            return signIn.Status switch
            {
                HttpStatusCode.OK => new(ConnectorEndpointTestOutcome.Ok, $"{label} API found - connection OK.", address),
                HttpStatusCode.Unauthorized => new(ConnectorEndpointTestOutcome.Failed, $"{label} API found, but the token was rejected.", address),
                HttpStatusCode.Forbidden => new(ConnectorEndpointTestOutcome.Failed, $"{label} API found, but the token was refused (403). It may lack scopes or single sign-on authorization.", address),
                _ => new(ConnectorEndpointTestOutcome.Failed, $"{label} API found, but the sign-in check failed ({signIn.Describe()}).", address)
            };
        }

        return new(ConnectorEndpointTestOutcome.Failed, WithLastError("Could not reach a GitHub API at this address", lastError));
    }

    private async Task<ConnectorEndpointTestResult> TestGitHubPackagesAsync(
        ConnectorUrlResolution resolution, string typed, string? userName, string? token, CancellationToken cancellationToken)
    {
        var owner = resolution.Owner!;
        var candidate = resolution.Candidates[0];
        var address = ConnectorUrlResolver.AddressToSave(resolution, typed, candidate);

        // GitHub Packages answers 401 without a token whether or not the owner exists, so there is nothing to learn yet.
        if (token == null)
            return new(ConnectorEndpointTestOutcome.NeedsToken, $"GitHub Packages feed for {owner} - enter a token to check access.", address, owner);

        var response = await SendAsync(candidate, BasicAuth(userName, token), cancellationToken);
        if (response.Status == HttpStatusCode.OK && HasNuGetResources(response.Body))
            return new(ConnectorEndpointTestOutcome.Ok, $"GitHub Packages feed for {owner} - connection OK.", address, owner);

        return response.Status switch
        {
            HttpStatusCode.NotFound => new(ConnectorEndpointTestOutcome.Failed, $"No packages feed found for {owner}. Check the organization or user name."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(ConnectorEndpointTestOutcome.Failed, $"The token was rejected for {owner}. It needs the read:packages scope.", address, owner),
            _ => new(ConnectorEndpointTestOutcome.Failed, WithLastError("Could not reach GitHub Packages", response.Describe()))
        };
    }

    private async Task<ConnectorEndpointTestResult> TestNuGetFeedAsync(
        ConnectorUrlResolution resolution, string typed, string? token, CancellationToken cancellationToken)
    {
        string? lastError = null;
        string? protectedCandidate = null;
        foreach (var candidate in resolution.Candidates)
        {
            var response = await SendAsync(candidate, FeedAuth(candidate, token), cancellationToken);
            if (response.Status == HttpStatusCode.OK && HasNuGetResources(response.Body))
            {
                var name = resolution.Kind == ConnectorEndpointKind.NuGetOrg ? "NuGet.org feed" : "NuGet feed";
                return new(ConnectorEndpointTestOutcome.Ok, $"{name} found - connection OK.", ConnectorUrlResolver.AddressToSave(resolution, typed, candidate));
            }

            if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                protectedCandidate ??= candidate;
            else
                lastError = response.Describe();
        }

        if (protectedCandidate != null)
        {
            var address = ConnectorUrlResolver.AddressToSave(resolution, typed, protectedCandidate);
            return token == null
                ? new(ConnectorEndpointTestOutcome.NeedsToken, "This feed needs a token - enter one and click Test again.", address)
                : new(ConnectorEndpointTestOutcome.Failed, "The feed rejected the token.", address);
        }

        return new(ConnectorEndpointTestOutcome.Failed, WithLastError("Could not find a NuGet feed at this address", lastError));
    }

    /// <summary>
    /// ProGet: the health page (no key needed) identifies the server, then the feed endpoint is read, first without
    /// the key (a public feed) and then with it. Without a feed name the key is used to list the server's NuGet
    /// feeds; a single feed is taken as the answer.
    /// </summary>
    private async Task<ConnectorEndpointTestResult> TestProGetAsync(
        ConnectorUrlResolution resolution, string? userName, string? apiKey, CancellationToken cancellationToken)
    {
        var server = resolution.ServerUrl!;
        var health = await SendAsync(server + "health?format=json", null, cancellationToken);
        if (!TryReadProGetHealth(health, out var version))
            return new(ConnectorEndpointTestOutcome.Failed, WithLastError("Could not find a ProGet server at this address", health.Describe()));

        var label = string.IsNullOrWhiteSpace(version) ? "ProGet" : $"ProGet {version}";
        var feed = resolution.Feed;
        IReadOnlyList<string> feeds = [];
        if (feed == null)
        {
            feeds = await ListProGetNuGetFeedsAsync(server, apiKey, cancellationToken);
            if (feeds.Count != 1)
            {
                var ask = feeds.Count > 1 ? "choose a feed" : "enter the feed name";
                return new(ConnectorEndpointTestOutcome.NeedsFeed, $"{label} found - {ask}.") { ServerUrl = server, Feeds = feeds };
            }

            feed = feeds[0];
        }

        var endpoint = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, server, feed).Candidates[0];
        ConnectorEndpointTestResult Found(ConnectorEndpointTestOutcome outcome, string message)
            => new(outcome, message, endpoint) { ServerUrl = server, Feed = feed, Feeds = feeds };

        var response = await SendAsync(endpoint, null, cancellationToken);
        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (apiKey == null)
                return Found(ConnectorEndpointTestOutcome.NeedsToken, $"{label} feed {feed} found - enter an API key to check access.");

            response = await SendAsync(endpoint, NuGetService.ProGetAuthorization(userName, apiKey), cancellationToken);
            if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Found(ConnectorEndpointTestOutcome.Failed, $"The API key was rejected for feed {feed}.");
        }

        if (response.Status == HttpStatusCode.OK && HasNuGetResources(response.Body))
            return Found(ConnectorEndpointTestOutcome.Ok, $"{label} feed {feed} found - connection OK.");

        if (response.Status == HttpStatusCode.NotFound)
        {
            if (feeds.Count == 0)
                feeds = await ListProGetNuGetFeedsAsync(server, apiKey, cancellationToken);
            return new(ConnectorEndpointTestOutcome.Failed, $"No NuGet feed named {feed} on this server.") { ServerUrl = server, Feed = feed, Feeds = feeds };
        }

        return new(ConnectorEndpointTestOutcome.Failed, WithLastError($"Could not read feed {feed}", response.Describe())) { ServerUrl = server, Feed = feed, Feeds = feeds };
    }

    /// <summary>
    /// The Feed dropdown of the connector dialog: the NuGet feeds the API key may list on the ProGet server behind
    /// <paramref name="serverInput"/>. The key is sent only after the health page has identified the server as ProGet.
    /// An empty list comes with the reason in <see cref="ProGetFeedListResult.Message"/>.
    /// </summary>
    public async Task<ProGetFeedListResult> ListProGetFeedsAsync(string? serverInput, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverInput))
            return new([], "Enter the Server URL to list the feeds.");

        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, serverInput);
        if (resolution.ServerUrl == null)
            return new([], resolution.Error ?? ConnectorUrlResolver.InvalidMessage);

        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (key == null)
            return new([], "Enter an API key to list the feeds, or type the feed name.");

        var server = resolution.ServerUrl;
        var health = await SendAsync(server + "health?format=json", null, cancellationToken);
        if (!TryReadProGetHealth(health, out _))
            return new([], WithLastError("Could not find a ProGet server at this address", health.Describe()));

        var feeds = await ListProGetNuGetFeedsAsync(server, key, cancellationToken);
        return feeds.Count == 0
            ? new([], "This API key may not list feeds - type the feed name.")
            : new(feeds);
    }

    /// <summary>
    /// The ProGet health page answers with JSON that names the application; a 500 is a ProGet with a health problem
    /// and still ProGet.
    /// </summary>
    private static bool TryReadProGetHealth(ProbeResponse health, out string? version)
    {
        version = null;
        if (health.Status is not (HttpStatusCode.OK or HttpStatusCode.InternalServerError) || !TryParseObject(health.Body, out var doc))
            return false;

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("applicationName", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString()?.Contains("ProGet", StringComparison.OrdinalIgnoreCase) != true)
                return false;

            foreach (var property in new[] { "versionNumber", "releaseNumber" })
            {
                if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    version = value.GetString();
                    break;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The NuGet feeds the API key may see (Feed Management API). Many keys may not list feeds, and ProGet answers an
    /// unknown key with an empty list, so an empty result only means "type the feed name".
    /// </summary>
    private async Task<IReadOnlyList<string>> ListProGetNuGetFeedsAsync(string server, string? apiKey, CancellationToken cancellationToken)
    {
        if (apiKey == null)
            return [];

        var response = await SendAsync(server + "api/management/feeds/list", null, cancellationToken, ("X-ApiKey", apiKey));
        if (response.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(response.Body))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()))
                    continue;
                if (item.TryGetProperty("feedType", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && !string.Equals(type.GetString(), "nuget", StringComparison.OrdinalIgnoreCase))
                    continue;

                names.Add(name.GetString()!);
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "The ProGet feed list was not JSON; the feed name has to be typed.");
            return [];
        }
    }

    /// <summary>
    /// A GitHub REST API root lists its URLs when it allows anonymous calls, and otherwise refuses with GitHub's own
    /// headers or error body (an Enterprise Server in private mode, or a rate limit).
    /// </summary>
    private static bool LooksLikeGitHubApi(ProbeResponse probe)
    {
        if (probe.Status == HttpStatusCode.OK)
            return HasJsonProperty(probe.Body, "current_user_url");

        if (probe.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return probe.HasGitHubHeaders || HasJsonProperty(probe.Body, "documentation_url");

        return false;
    }

    private static string GitHubLabel(ConnectorEndpointKind? kind, string? enterpriseVersion)
        => kind switch
        {
            ConnectorEndpointKind.GitHubEnterpriseCloud => "GitHub Enterprise Cloud",
            ConnectorEndpointKind.GitHubEnterpriseServer => string.IsNullOrWhiteSpace(enterpriseVersion)
                ? "GitHub Enterprise Server"
                : $"GitHub Enterprise Server {enterpriseVersion.Trim()}",
            _ => "GitHub"
        };

    /// <summary>Same sign-in as <see cref="NuGetService"/>: GitHub Packages takes Basic with any user name and the token as password.</summary>
    private static AuthenticationHeaderValue BasicAuth(string? userName, string token)
    {
        var user = string.IsNullOrWhiteSpace(userName) ? "USER" : userName.Trim();
        return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
    }

    /// <summary>Same rule as <see cref="NuGetService"/>, so the test tells what the saved connector will see: only a ProGet feed is sent the token.</summary>
    private static AuthenticationHeaderValue? FeedAuth(string candidate, string? token)
        => token != null && ConnectorHelpers.IsProGet(candidate) ? new("Bearer", token) : null;

    private static bool HasNuGetResources(string? body)
        => TryParseObject(body, out var doc) && Using(doc, root => root.TryGetProperty("resources", out var r) && r.ValueKind == JsonValueKind.Array);

    private static bool HasJsonProperty(string? body, string name)
        => TryParseObject(body, out var doc) && Using(doc, root => root.TryGetProperty(name, out _));

    private static bool TryParseObject(string? body, out JsonDocument doc)
    {
        doc = null!;
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON (an HTML page, or an answer cut at the size cap): not the API we are looking for.
            return false;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return true;
        doc.Dispose();
        return false;
    }

    private static bool Using(JsonDocument doc, Func<JsonElement, bool> check)
    {
        using (doc)
            return check(doc.RootElement);
    }

    private static string WithLastError(string message, string? lastError)
        => string.IsNullOrWhiteSpace(lastError) ? message + "." : $"{message} ({lastError}).";

    private async Task<ProbeResponse> SendAsync(
        string url,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken,
        (string Name, string Value)? header = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("GrayMoon");
            request.Headers.Authorization = authorization;
            if (header is { } extra)
                request.Headers.TryAddWithoutValidation(extra.Name, extra.Value);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadCappedAsync(response.Content, timeout.Token);
            var hasGitHubHeaders = response.Headers.Contains("X-GitHub-Request-Id") || response.Headers.Contains("X-GitHub-Media-Type");
            var enterpriseVersion = response.Headers.TryGetValues("X-GitHub-Enterprise-Version", out var versions) ? versions.FirstOrDefault() : null;
            logger.LogDebug("Connector endpoint probe {Url} -> {StatusCode}.", url, (int)response.StatusCode);
            return new ProbeResponse(response.StatusCode, body, hasGitHubHeaders, enterpriseVersion, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Connector endpoint probe {Url} timed out.", url);
            return ProbeResponse.Unreachable("no answer in time");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            logger.LogDebug(ex, "Connector endpoint probe {Url} failed.", url);
            return ProbeResponse.Unreachable(ex.Message);
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private readonly record struct ProbeResponse(
        HttpStatusCode? Status,
        string? Body,
        bool HasGitHubHeaders,
        string? EnterpriseVersion,
        string? Error)
    {
        public static ProbeResponse Unreachable(string error) => new(null, null, false, null, error);

        public string Describe() => Status is { } status ? $"HTTP {(int)status}" : Error ?? "no answer";
    }
}
