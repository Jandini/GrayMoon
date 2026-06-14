using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Models;
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

    /// <summary>Retries transient failures for GitHub REST <strong>mutations</strong> (POST). Read-only calls use <see cref="GitHubGetRetryPipeline"/>.</summary>
    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubMutationRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
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

    /// <summary>
    /// Retry pipeline for live-feed polls: retries 502/503 but NOT 429 —
    /// the polling loop owns rate-limit backoff so retrying here just wastes quota.
    /// </summary>
    private static readonly ResiliencePipeline<HttpResponseMessage> GitHubLiveFeedRetryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                MaxRetryAttempts = 2,
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

    /// <summary>Lists workflows for a repo using the connector token (workspace / multi-connector actions).</summary>
    public async Task<List<GitHubWorkflowDto>> GetWorkflowsAsync(Connector connector, string owner, string repo, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        var response = await GetAsync<GitHubWorkflowsResponse>(connector, $"repos/{owner}/{repo}/actions/workflows", cancellationToken);
        return response?.Workflows ?? new List<GitHubWorkflowDto>();
    }

    /// <summary>GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}. Returns null if not found.</summary>
    public async Task<GitHubWorkflowDto?> GetWorkflowByIdAsync(Connector connector, string owner, string repo, long workflowId, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        using var response = await GetResponseAsync(connector, $"repos/{owner}/{repo}/actions/workflows/{workflowId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<GitHubWorkflowDto>(_jsonOptions, cancellationToken);
    }

    /// <summary>Loads a repository file as UTF-8 text (e.g. workflow YAML). Returns null if missing or not a file.</summary>
    public async Task<string?> GetRepositoryFileUtf8TextAsync(Connector connector, string owner, string repo, string filePath, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var encodedPath = string.Join("/", filePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        using var response = await GetResponseAsync(connector, $"repos/{owner}/{repo}/contents/{encodedPath}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        var dto = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(_jsonOptions, cancellationToken);
        if (dto == null || !string.Equals(dto.Type, "file", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(dto.Encoding, "base64", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(dto.Content))
            return null;

        try
        {
            var raw = dto.Content.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
            var bytes = Convert.FromBase64String(raw);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Could not decode repository file as base64. Path={Path}", filePath);
            return null;
        }
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

    /// <summary>Returns the most recent workflow runs for a specific branch (up to <paramref name="perPage"/> results).</summary>
    public async Task<List<GitHubWorkflowRunDto>> GetWorkflowRunsForBranchAsync(Connector connector, string owner, string repo, string branch, int perPage = 20)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch is required.", nameof(branch));

        var encodedBranch = Uri.EscapeDataString(branch);
        var response = await GetAsync<GitHubWorkflowRunsResponse>(
            connector,
            $"repos/{owner}/{repo}/actions/runs?branch={encodedBranch}&per_page={perPage}");

        return response?.WorkflowRuns ?? new List<GitHubWorkflowRunDto>();
    }

    /// <summary>Lists jobs for a workflow run (steps, status).</summary>
    public async Task<GitHubWorkflowJobsResponse?> GetWorkflowRunJobsAsync(Connector connector, string owner, string repo, long runId, CancellationToken cancellationToken = default, bool skipRateLimitRetry = false)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (runId <= 0)
            throw new ArgumentException("Run id is required.", nameof(runId));

        return await GetAsync<GitHubWorkflowJobsResponse>(
            connector,
            $"repos/{owner}/{repo}/actions/runs/{runId}/jobs?per_page=100",
            cancellationToken,
            skipRateLimitRetry);
    }

    /// <summary>Downloads the plain-text log for a single job (follows the 302 redirect to the CDN URL). Returns null if unavailable.</summary>
    public async Task<string?> GetJobLogsAsync(Connector connector, string owner, string repo, long jobId, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (jobId <= 0)
            throw new ArgumentException("Job id is required.", nameof(jobId));

        using var response = await GetResponseAsync(connector, $"repos/{owner}/{repo}/actions/jobs/{jobId}/logs", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetJobLogsAsync: HTTP {StatusCode} for job {JobId}", (int)response.StatusCode, jobId);
            return null;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("GetJobLogsAsync: {Length} chars for job {JobId}", text.Length, jobId);
        return text;
    }

    public async Task RerunWorkflowRunAsync(Connector connector, string owner, string repo, long runId, CancellationToken cancellationToken = default)
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

        await PostAsync(connector, $"repos/{owner}/{repo}/actions/runs/{runId}/rerun", payload: null, cancellationToken: cancellationToken);
    }

    public async Task RerunJobAsync(Connector connector, string owner, string repo, long jobId, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        await PostAsync(connector, $"repos/{owner}/{repo}/actions/jobs/{jobId}/rerun", payload: null, cancellationToken: cancellationToken);
    }

    public async Task RerunFailedJobsAsync(Connector connector, string owner, string repo, long runId, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (runId <= 0)
            throw new ArgumentException("Workflow run id is required.", nameof(runId));

        await PostAsync(connector, $"repos/{owner}/{repo}/actions/runs/{runId}/rerun-failed-jobs", payload: null, cancellationToken: cancellationToken);
    }

    /// <summary>POST /repos/{owner}/{repo}/actions/runs/{run_id}/cancel - cancels an in-progress workflow run.</summary>
    /// <remarks>409 when the run already finished is treated as success (UI/API lag vs GitHub).</remarks>
    public async Task CancelWorkflowRunAsync(Connector connector, string owner, string repo, long runId, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);

        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (runId <= 0)
            throw new ArgumentException("Workflow run id is required.", nameof(runId));

        var requestUri = $"repos/{owner}/{repo}/actions/runs/{runId}/cancel";
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePostRequest(connector, requestUri, payload: null);
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }, cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (IsCancelWorkflowRunAlreadyCompleted(response.StatusCode, errorContent))
        {
            _logger.LogDebug(
                "GitHub cancel: run already completed (409), refreshing state. URL: {Url}",
                new Uri(new Uri(baseUrl), requestUri));
            return;
        }

        _logger.LogError("GitHub API POST failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
            response.StatusCode,
            new Uri(new Uri(baseUrl), requestUri),
            errorContent);

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }

    public async Task DispatchWorkflowAsync(Connector connector, string owner, string repo, long workflowId, string branch, CancellationToken cancellationToken = default)
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
        await PostAsync(connector, $"repos/{owner}/{repo}/actions/workflows/{workflowId}/dispatches", payload, cancellationToken);
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(Connector connector)
    {
        EnsureConnectorConfigured(connector);

        try
        {
            var organizations = await GetAsync<List<GitHubOrganizationDto>>(connector, "user/orgs")
                ?? new List<GitHubOrganizationDto>();
            var repositories = await GetRepositoriesAsync(connector);
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

    public async Task<(int OrganizationCount, int RepositoryCount)> TestConnectionDetailedAsync(Connector connector)
    {
        EnsureConnectorConfigured(connector);

        var organizations = await GetAsync<List<GitHubOrganizationDto>>(connector, "user/orgs")
            ?? new List<GitHubOrganizationDto>();
        var repositories = await GetRepositoriesAsync(connector);

        return (organizations.Count, repositories.Count);
    }

    /// <summary>Gets the pull request for the given branch in the repo, if any. Fetches up to 5 and returns the first one opened by a human (user.type != "Bot"), falling back to the first match when all are bots. Returns null when no PR or API error.</summary>
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
        var requestUri = $"repos/{owner}/{repo}/pulls?state=all&head={head}&per_page=5";

        try
        {
            var list = await GetAsync<List<GitHubPullRequestDto>>(connector, requestUri, cancellationToken);
            if (list == null || list.Count == 0)
                return null;

            var matching = list.Where(pr => pr.Head == null || string.Equals(pr.Head.Ref, branch, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
                return null;

            // Prefer a PR opened by a human; fall back to first match if all are bots
            return matching.FirstOrDefault(pr => !string.Equals(pr.User?.Type, "Bot", StringComparison.OrdinalIgnoreCase))
                ?? matching[0];
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

    /// <summary>Creates a pull request via POST /repos/{owner}/{repo}/pulls. Throws on non-success.</summary>
    public async Task<GitHubPullRequestDto> CreatePullRequestAsync(
        Connector connector,
        string owner,
        string repo,
        GitHubCreatePullRequestRequestDto body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        ArgumentNullException.ThrowIfNull(body);

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/pulls";
        var payload = JsonSerializer.Serialize(body);

        var dto = await PostAsync<GitHubPullRequestDto>(connector, requestUri, payload, cancellationToken);
        if (dto == null)
            throw new InvalidOperationException("GitHub returned an empty response when creating a pull request.");
        return dto;
    }

    /// <summary>Requests reviewers via POST /repos/{owner}/{repo}/pulls/{pull_number}/requested_reviewers. Throws on non-success.</summary>
    public async Task RequestReviewersAsync(
        Connector connector,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
        IReadOnlyList<string> teamReviewers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));
        if (pullNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullNumber));

        EnsureConnectorConfigured(connector);

        var body = new GitHubRequestReviewersRequestDto
        {
            Reviewers = reviewers?.Where(static r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>(),
            TeamReviewers = teamReviewers?.Where(static r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>()
        };
        if (body.Reviewers.Count == 0 && body.TeamReviewers.Count == 0)
            return;

        var requestUri = $"repos/{owner}/{repo}/pulls/{pullNumber}/requested_reviewers";
        var payload = JsonSerializer.Serialize(body);
        await PostAsync(connector, requestUri, payload, cancellationToken);
    }

    /// <summary>Lists teams via GET /repos/{owner}/{repo}/teams. Returns empty list on 403/404.</summary>
    public async Task<List<GitHubTeamDto>> GetTeamsAsync(
        Connector connector,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/teams?per_page=100";

        try
        {
            return await GetAsync<List<GitHubTeamDto>>(connector, requestUri, cancellationToken)
                ?? new List<GitHubTeamDto>();
        }
        catch (GitHubHttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _logger.LogDebug(ex, "GitHub API list teams failed (treated as empty). Owner={Owner}, Repo={Repo}", owner, repo);
            return new List<GitHubTeamDto>();
        }
    }

    /// <summary>Lists collaborators via GET /repos/{owner}/{repo}/collaborators. Returns empty list on 403/404.</summary>
    public async Task<List<GitHubCollaboratorDto>> GetCollaboratorsAsync(
        Connector connector,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        EnsureConnectorConfigured(connector);

        var requestUri = $"repos/{owner}/{repo}/collaborators?affiliation=all&per_page=100";

        try
        {
            return await GetAsync<List<GitHubCollaboratorDto>>(connector, requestUri, cancellationToken)
                ?? new List<GitHubCollaboratorDto>();
        }
        catch (GitHubHttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _logger.LogDebug(ex, "GitHub API list collaborators failed (treated as empty). Owner={Owner}, Repo={Repo}", owner, repo);
            return new List<GitHubCollaboratorDto>();
        }
    }

    private async Task<T?> PostAsync<T>(Connector connector, string requestUri, string? payload, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePostRequest(connector, requestUri, payload);
            return await _httpClient.SendAsync(request, ct);
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API POST failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                new Uri(new Uri(baseUrl), requestUri),
                errorContent);
            throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connector token is not configured.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Connector connector, string requestUri, CancellationToken cancellationToken, bool skipRateLimitRetry = false)
    {
        var pipeline = skipRateLimitRetry ? GitHubLiveFeedRetryPipeline : GitHubGetRetryPipeline;
        return await pipeline.ExecuteAsync(async (ct) =>
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
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private async Task<T?> GetAsync<T>(Connector connector, string requestUri, CancellationToken cancellationToken = default, bool skipRateLimitRetry = false)
    {
        using var response = await GetResponseAsync(connector, requestUri, cancellationToken, skipRateLimitRetry);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    private HttpRequestMessage CreatePostRequest(Connector connector, string requestUri, string? payload)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), requestUri));
        if (payload != null)
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        else
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("GrayMoon");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = ConnectorHelpers.UnprotectToken(connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connector token is not configured.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task PostAsync(Connector connector, string requestUri, string? payload = null, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(connector.ApiBaseUrl)
            ? "https://api.github.com/"
            : connector.ApiBaseUrl.TrimEnd('/') + "/";

        using var response = await GitHubMutationRetryPipeline.ExecuteAsync(async ct =>
        {
            using var request = CreatePostRequest(connector, requestUri, payload);
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }, cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("GitHub API POST failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
            response.StatusCode,
            new Uri(new Uri(baseUrl), requestUri),
            errorContent);

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }

    private static void ThrowGitHubApiFailure(HttpResponseMessage response, string errorContent)
    {
        if (GitHubApiErrorHelper.IsRateLimitExhausted(response)
            && !GitHubApiErrorHelper.LooksLikeRateLimit(response.StatusCode, GitHubApiErrorHelper.TryParseGitHubApiUserMessage(errorContent)))
        {
            throw GitHubApiErrorHelper.CreateHttpRequestException(
                response.StatusCode,
                "API rate limit exceeded. X-RateLimit-Remaining is 0.",
                response);
        }

        throw GitHubApiErrorHelper.CreateHttpRequestException(response.StatusCode, errorContent, response);
    }

    private static bool IsCancelWorkflowRunAlreadyCompleted(HttpStatusCode status, string errorContent)
    {
        if (status != HttpStatusCode.Conflict)
            return false;
        var msg = GitHubApiErrorHelper.TryParseGitHubApiUserMessage(errorContent);
        return msg != null
               && msg.Contains("Cannot cancel a workflow run that is completed", StringComparison.OrdinalIgnoreCase);
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
