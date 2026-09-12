using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Actions workflows, runs, jobs, logs, and the dispatch/rerun/cancel mutations. Also owns the one repository-content read (<see cref="GetRepositoryFileUtf8TextAsync"/>) whose only consumer is workflow_dispatch YAML detection.</summary>
public sealed partial class GitHubService
{
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
        var response = await GetETaggedAsync<GitHubWorkflowRunsResponse>(
            connector,
            $"repos/{owner}/{repo}/actions/runs?branch={encodedBranch}&per_page={perPage}",
            default);

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

        return await GetETaggedAsync<GitHubWorkflowJobsResponse>(
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

    private static bool IsCancelWorkflowRunAlreadyCompleted(HttpStatusCode status, string errorContent)
    {
        if (status != HttpStatusCode.Conflict)
            return false;
        var msg = GitHubApiErrorHelper.TryParseGitHubApiUserMessage(errorContent);
        return msg != null
               && msg.Contains("Cannot cancel a workflow run that is completed", StringComparison.OrdinalIgnoreCase);
    }
}
