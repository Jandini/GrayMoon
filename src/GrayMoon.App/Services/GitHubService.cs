using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GrayMoon.App.Models;
using Polly;
using Polly.Retry;

namespace GrayMoon.App.Services;

public class GitHubService : IConnectorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;
    private readonly GitHubOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConnectorType ConnectorType => ConnectorType.GitHub;

    public GitHubService(HttpClient httpClient, IConfiguration configuration, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubGetRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                // 3 quick retries with short backoff, then fail
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = static args =>
                {
                    if (args.Outcome.Result is HttpResponseMessage prev)
                        prev.Dispose();
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    public async Task<List<GitHubOrganizationDto>> GetOrganizationsAsync()
    {
        EnsureConfigured();
        return await GetAsync<List<GitHubOrganizationDto>>("user/orgs") ?? new List<GitHubOrganizationDto>();
    }

    public async Task<List<GitHubRepositoryDto>> GetRepositoriesAsync()
    {
        EnsureConfigured();
        return await GetRepositoriesPagedAsync("user/repos?visibility=all");
    }

    public async Task<List<GitHubRepositoryDto>> GetRepositoriesAsync(Connector connector, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        return await GetRepositoriesPagedAsync(connector, "user/repos?visibility=all", progress, batchProgress, cancellationToken);
    }

    public async Task<List<GitHubWorkflowDto>> GetWorkflowsAsync(string owner, string repo)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Owner is required.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository is required.", nameof(repo));
        }

        var response = await GetAsync<GitHubWorkflowsResponse>($"repos/{owner}/{repo}/actions/workflows");
        return response?.Workflows ?? new List<GitHubWorkflowDto>();
    }

    public async Task<GitHubWorkflowRunDto?> GetLatestWorkflowRunAsync(Connector connector, string owner, string repo)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Owner is required.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository is required.", nameof(repo));
        }

        var response = await GetAsync<GitHubWorkflowRunsResponse>(
            connector,
            $"repos/{owner}/{repo}/actions/runs?per_page=1");

        return response?.WorkflowRuns.FirstOrDefault();
    }

    public async Task RerunWorkflowRunAsync(Connector connector, string owner, string repo, long runId)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Owner is required.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository is required.", nameof(repo));
        }

        if (runId <= 0)
        {
            throw new ArgumentException("Workflow run id is required.", nameof(runId));
        }

        await PostAsync(connector, $"repos/{owner}/{repo}/actions/runs/{runId}/rerun");
    }

    public async Task DispatchWorkflowAsync(Connector connector, string owner, string repo, long workflowId, string branch)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Owner is required.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository is required.", nameof(repo));
        }

        if (workflowId <= 0)
        {
            throw new ArgumentException("Workflow id is required.", nameof(workflowId));
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new ArgumentException("Branch is required.", nameof(branch));
        }

        var payload = JsonSerializer.Serialize(new { @ref = branch });
        await PostAsync(connector, $"repos/{owner}/{repo}/actions/workflows/{workflowId}/dispatches", payload);
    }

    public async Task<bool> TestConnectionAsync(Connector connector)
    {
        EnsureConnectorConfigured(connector);

        try
        {
            var organizations = await GetAsync<List<GitHubOrganizationDto>>(connector, "user/orgs")
                ?? new List<GitHubOrganizationDto>();
            var repositories = await GetRepositoriesAsync(connector);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test GitHub connector connection.");
            return false;
        }
    }

    public async Task<(int OrganizationCount, int RepositoryCount)> TestConnectionDetailedAsync(Connector connector)
    {
        EnsureConnectorConfigured(connector);

        var organizations = await GetAsync<List<GitHubOrganizationDto>>(connector, "user/orgs")
            ?? new List<GitHubOrganizationDto>();
        var repositories = await GetRepositoriesAsync(connector);

        return (organizations.Count, repositories.Count);
    }

    /// <summary>Gets the pull request for the given branch in the repo, if any. Uses GET /repos/{owner}/{repo}/pulls?state=all&amp;head=owner:branch&amp;per_page=1. Returns null when no PR or API error.</summary>
    public async Task<GitHubPullRequestDto?> GetPullRequestForBranchAsync(Connector connector, string owner, string repo, string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(branch))
            return null;

        EnsureConnectorConfigured(connector);

        var head = $"{owner}:{Uri.EscapeDataString(branch)}";
        var requestUri = $"repos/{owner}/{repo}/pulls?state=all&head={head}&per_page=1";

        try
        {
            var list = await GetAsync<List<GitHubPullRequestDto>>(connector, requestUri, cancellationToken);
            var pr = list?.FirstOrDefault();
            if (pr == null)
                return null;
            if (pr.Head != null && !string.Equals(pr.Head.Ref, branch, StringComparison.OrdinalIgnoreCase))
                return null;
            return pr;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get PR for branch failed. Owner={Owner}, Repo={Repo}, Branch={Branch}", owner, repo, branch);
            return null;
        }
    }

    /// <summary>Gets a single pull request by number. Uses GET /repos/{owner}/{repo}/pulls/{pull_number}. Returns null on API error.</summary>
    public async Task<GitHubPullRequestDto?> GetPullRequestByNumberAsync(Connector connector, string owner, string repo, int pullNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            return null;

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}";

        try
        {
            return await GetAsync<GitHubPullRequestDto>(connector, requestUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub API get PR by number failed. Owner={Owner}, Repo={Repo}, PullNumber={PullNumber}", owner, repo, pullNumber);
            return null;
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

    private static HttpRequestMessage CreateGetRequest(Connector connector, string requestUri)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), requestUri));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Connector connector, string requestUri, CancellationToken cancellationToken)
    {
        return await GitHubGetRetryPipeline.ExecuteAsync(async (ct) =>
        {
            using var request = CreateGetRequest(connector, requestUri);
            return await _httpClient.SendAsync(request, ct);
        }, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string requestUri)
    {
        var response = await _httpClient.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(_httpClient.BaseAddress ?? new Uri("https://api.github.com/"), requestUri),
                errorContent);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private async Task<T?> GetAsync<T>(Connector connector, string requestUri, CancellationToken cancellationToken = default)
    {
        using var response = await GetResponseAsync(connector, requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    private async Task PostAsync(Connector connector, string requestUri, string? payload = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), requestUri));
        if (payload != null)
        {
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(new Uri(baseUrl), requestUri),
                errorContent);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<List<GitHubRepositoryDto>> GetRepositoriesPagedAsync(string requestUri)
    {
        var results = new List<GitHubRepositoryDto>();
        const int pageSize = 20;
        var page = 1;

        while (true)
        {
            var pageUri = $"{requestUri}&per_page={pageSize}&page={page}";
            var pageItems = await GetAsync<List<GitHubRepositoryDto>>(pageUri) ?? new List<GitHubRepositoryDto>();

            results.AddRange(pageItems);

            if (pageItems.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return results;
    }

    private async Task<List<GitHubRepositoryDto>> GetRepositoriesPagedAsync(Connector connector, string requestUri, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<GitHubRepositoryDto>();
        const int pageSize = 20;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageUri = $"{requestUri}&per_page={pageSize}&page={page}";
            var pageItems = await GetAsync<List<GitHubRepositoryDto>>(connector, pageUri, cancellationToken) ?? new List<GitHubRepositoryDto>();

            results.AddRange(pageItems);
            progress?.Report(results.Count);
            if (pageItems.Count > 0)
                batchProgress?.Report(pageItems);

            if (pageItems.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return results;
    }
}
