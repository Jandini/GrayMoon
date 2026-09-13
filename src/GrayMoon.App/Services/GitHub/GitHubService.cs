using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;
using Polly;
using Polly.Retry;

namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// GitHub REST API client. Split into partial-class files by concern:
/// <see cref="GitHubService"/> (this file) - construction and the <c>IConnectorService</c> surface
/// (connector type, connection test, connector/PAT configuration guards);
/// <c>GitHubService.Http.cs</c> - shared HTTP transport (retry pipelines, request builders, generic
/// GET/POST/PUT/PATCH helpers, ETag-conditional GET, error translation);
/// <c>GitHubService.Repositories.cs</c> - organization/repository listing and paging;
/// <c>GitHubService.Workflows.cs</c> - Actions workflows, runs, jobs, logs, dispatch/rerun/cancel;
/// <c>GitHubService.PullRequests.cs</c> - pull requests, reviews, merge, teams/collaborators, check runs.
/// </summary>
public sealed partial class GitHubService : IConnectorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;
    private readonly GitHubOptions _options;
    private readonly IGitHubRateLimitTracker _rateLimitTracker;
    private readonly IGitHubETagCache _eTagCache;
    private readonly IGitHubApiUsageRecorder _usageRecorder;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConnectorType ConnectorType => ConnectorType.GitHub;

    public GitHubService(HttpClient httpClient, IConfiguration configuration, IGitHubRateLimitTracker rateLimitTracker, IGitHubETagCache eTagCache, IGitHubApiUsageRecorder usageRecorder, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rateLimitTracker = rateLimitTracker ?? throw new ArgumentNullException(nameof(rateLimitTracker));
        _eTagCache = eTagCache ?? throw new ArgumentNullException(nameof(eTagCache));
        _usageRecorder = usageRecorder ?? throw new ArgumentNullException(nameof(usageRecorder));
        _options = configuration.GetSection("GitHub").Get<GitHubOptions>() ?? new GitHubOptions();

        var baseUrl = string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
            ? "https://api.github.com/"
            : _options.ApiBaseUrl.TrimEnd('/') + "/";

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GrayMoon");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.PersonalAccessToken);
        }
    }

    /// <summary>
    /// Verifies the token is valid with a single, cheap call instead of listing every visible org and repo
    /// (which could be hundreds of API calls for a large account/organization).
    /// </summary>
    public async Task<ConnectorTestResult> TestConnectionAsync(Connector connector)
    {
        EnsureConnectorConfigured(connector);

        try
        {
            await GetAsync<GitHubUserDto>(connector, "user");
            return ConnectorTestResult.Ok();
        }
        catch (HttpRequestException ex)
        {
            var message = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);
            _logger.LogError(ex, "Failed to test GitHub connector connection. Status={StatusCode}", ex.StatusCode);
            var isConnectorFault = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
            return isConnectorFault ? ConnectorTestResult.Fault(message) : ConnectorTestResult.Fail(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test GitHub connector connection.");
            return ConnectorTestResult.Fail($"Connection error: {ex.Message}");
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
        {
            throw new InvalidOperationException("GitHub:PersonalAccessToken is not configured.");
        }
    }

    private static void EnsureConnectorConfigured(Connector connector)
    {
        if (connector.ConnectorType != ConnectorType.GitHub)
        {
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by GitHubService.");
        }

        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Connector token is not configured.");
        }
    }
}
