using System.Net.Http.Headers;
using System.Text.Json;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public class GitHubService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;
    private readonly GitHubOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    public async Task<List<GitHubRepositoryDto>> GetRepositoriesAsync(GitHubConnector connector, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
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

    public async Task<GitHubWorkflowRunDto?> GetLatestWorkflowRunAsync(GitHubConnector connector, string owner, string repo)
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

    public async Task RerunWorkflowRunAsync(GitHubConnector connector, string owner, string repo, long runId)
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

    public async Task DispatchWorkflowAsync(GitHubConnector connector, string owner, string repo, long workflowId, string branch)
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

    public async Task<(int OrganizationCount, int RepositoryCount)> TestConnectionAsync(GitHubConnector connector)
    {
        EnsureConnectorConfigured(connector);

        var organizations = await GetAsync<List<GitHubOrganizationDto>>(connector, "user/orgs")
            ?? new List<GitHubOrganizationDto>();
        var repositories = await GetRepositoriesAsync(connector);

        return (organizations.Count, repositories.Count);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
        {
            throw new InvalidOperationException("GitHub:PersonalAccessToken is not configured.");
        }
    }

    private static void EnsureConnectorConfigured(GitHubConnector connector)
    {
        if (string.IsNullOrWhiteSpace(connector.UserToken))
        {
            throw new InvalidOperationException("Connector token is not configured.");
        }
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

    private async Task<T?> GetAsync<T>(GitHubConnector connector, string requestUri, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), requestUri));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.UserToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(new Uri(baseUrl), requestUri),
                errorContent);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private async Task PostAsync(GitHubConnector connector, string requestUri, string? payload = null)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.UserToken);

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

    private async Task<List<GitHubRepositoryDto>> GetRepositoriesPagedAsync(GitHubConnector connector, string requestUri, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
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
