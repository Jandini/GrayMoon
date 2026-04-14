using System.Collections.Concurrent;
using System.Text.Json;
using GrayMoon.App.Models.Api;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles branch-related operations: common branches, create branches, checkout, and sync-to-default.
/// Stateless; UI state is owned by the caller.
/// </summary>
public sealed class WorkspaceBranchHandler(
    IServiceScopeFactory serviceScopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkspaceBranchHandler> logger)
{
    private const int DefaultMaxParallelOperations = 16;

    public async Task<CommonBranchesApiResult?> GetCommonBranchesAsync(int workspaceId, string apiBaseUrl, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        var request = new { workspaceId };
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/common", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Could not load common branches: {StatusCode}, {Error}", response.StatusCode, errorText);
            return null;
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CommonBranchesApiResult>(responseContent, AgentResponseJson.Options);
    }

    public async Task CreateBranchesAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        Action<int, int> reportProgress,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        await workspaceGitService.CreateBranchesAsync(
            workspaceId,
            newBranchName,
            baseBranch,
            onProgress: (completed, total) => reportProgress(completed, total),
            repositoryIds: null,
            cancellationToken: cancellationToken);
    }

    public async Task<(bool Success, string? Error)> CreateSingleBranchAsync(
        int workspaceId,
        int repositoryId,
        string newBranchName,
        string baseBranch,
        bool setUpstream,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        var apiRequest = new
        {
            workspaceId,
            repositoryId,
            newBranchName,
            baseBranch
        };
        var json = JsonSerializer.Serialize(apiRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/create", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Create branch failed: {StatusCode}, {Error}", response.StatusCode, errorText);
            return (false, "Failed to create branch.");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<CreateBranchApiResult>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result is null || !result.Success)
            return (false, result?.Error ?? "Failed to create branch.");

        if (setUpstream)
        {
            var upstreamRequest = new
            {
                workspaceId,
                repositoryId,
                branchName = newBranchName
            };
            var upstreamJson = JsonSerializer.Serialize(upstreamRequest);
            using var upstreamContent = new StringContent(upstreamJson, System.Text.Encoding.UTF8, "application/json");
            using var upstreamResponse = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/set-upstream", upstreamContent, cancellationToken);
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                var upstreamError = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Set upstream failed: {StatusCode}, {Error}", upstreamResponse.StatusCode, upstreamError);
                return (true, "Branch created but failed to set upstream.");
            }

            var upstreamResponseContent = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
            var upstreamResult = JsonSerializer.Deserialize<CreateBranchApiResult>(upstreamResponseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (upstreamResult != null && !upstreamResult.Success)
                return (true, upstreamResult.Error ?? "Branch created but failed to set upstream.");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(
        int workspaceId,
        int repositoryId,
        string branchName,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        var apiRequest = new
        {
            repositoryId,
            workspaceId,
            branchName
        };
        var json = JsonSerializer.Serialize(apiRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/checkout", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<CheckoutBranchResponse>(responseContent, AgentResponseJson.Options);

            if (result != null && !result.Success)
            {
                return (false, !string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage : "Failed to checkout branch.");
            }

            return (true, null);
        }
        else
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText) ?? $"Failed to checkout branch: {response.StatusCode}";
            logger.LogError("Checkout failed: {StatusCode}, {Error}", response.StatusCode, errorText);
            return (false, message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> SyncToDefaultSingleAsync(
        int workspaceId,
        int repositoryId,
        string? currentBranchName,
        bool hasUpstream,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        var baseUrl = apiBaseUrl;

        if (hasUpstream)
        {
            var deleteRequest = new { workspaceId, repositoryId, branchName = currentBranchName, isRemote = true };
            var deleteJson = JsonSerializer.Serialize(deleteRequest);
            using var deleteContent = new StringContent(deleteJson, System.Text.Encoding.UTF8, "application/json");
            using var deleteResponse = await httpClient.PostAsync($"{baseUrl}/api/branches/delete", deleteContent, cancellationToken);
            if (!deleteResponse.IsSuccessStatusCode)
            {
                var deleteError = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
                if (deleteError.IndexOf("not exist", StringComparison.OrdinalIgnoreCase) < 0 &&
                    deleteError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    logger.LogWarning("Delete remote branch failed for repo {RepositoryId}: {StatusCode}", repositoryId, deleteResponse.StatusCode);
                }
            }
        }

        var apiRequest = new { workspaceId, repositoryId, currentBranchName };
        var json = JsonSerializer.Serialize(apiRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{baseUrl}/api/branches/sync-to-default", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var errorText2 = await response.Content.ReadAsStringAsync(cancellationToken);
        var errMsg = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText2) ?? $"Failed to sync to default branch: {response.StatusCode}";
        logger.LogError("SyncToDefault failed for repo {RepositoryId}: {StatusCode}, {Error}", repositoryId, response.StatusCode, errorText2);
        return (false, errMsg);
    }

    public async Task<WorkspaceBranchBulkResult> FetchBranchesForWorkspaceAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        string apiBaseUrl,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        var total = repositoryIds.Count;
        var completed = 0;
        var errors = new ConcurrentDictionary<int, string>();
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelOperations, DefaultMaxParallelOperations);
        var httpClient = httpClientFactory.CreateClient();

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var apiRequest = new { workspaceId, repositoryId };
                var json = JsonSerializer.Serialize(apiRequest);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/refresh", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    errors[repositoryId] = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText)
                        ?? $"Failed to fetch branches: {response.StatusCode}";
                    return;
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<BranchesResponse>(responseContent, AgentResponseJson.Options);
                if (result?.Success == false)
                    errors[repositoryId] = result.ErrorMessage ?? "Failed to fetch branches.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fetch branches failed for repository {RepositoryId}", repositoryId);
                errors[repositoryId] = "Failed to fetch branches.";
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                reportProgress?.Invoke(done, total);
            }
        });

        await Task.WhenAll(tasks);
        var failureCount = errors.Count;
        return new WorkspaceBranchBulkResult(total - failureCount, failureCount, new Dictionary<int, string>(errors));
    }

    public async Task<WorkspaceBranchBulkResult> CheckoutBranchForWorkspaceAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        string branchName,
        string apiBaseUrl,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        var total = repositoryIds.Count;
        var completed = 0;
        var errors = new ConcurrentDictionary<int, string>();
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelOperations, DefaultMaxParallelOperations);
        var httpClient = httpClientFactory.CreateClient();

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var apiRequest = new { workspaceId, repositoryId, branchName };
                var json = JsonSerializer.Serialize(apiRequest);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync($"{apiBaseUrl}/api/branches/checkout", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    errors[repositoryId] = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText)
                        ?? $"Failed to checkout branch: {response.StatusCode}";
                    return;
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<CheckoutBranchResponse>(responseContent, AgentResponseJson.Options);
                if (result is { Success: false })
                    errors[repositoryId] = result.ErrorMessage ?? "Failed to checkout branch.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Checkout failed for repository {RepositoryId}", repositoryId);
                errors[repositoryId] = "Failed to checkout branch.";
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                reportProgress?.Invoke(done, total);
            }
        });

        await Task.WhenAll(tasks);
        var failureCount = errors.Count;
        return new WorkspaceBranchBulkResult(total - failureCount, failureCount, new Dictionary<int, string>(errors));
    }
}

public sealed record WorkspaceBranchBulkResult(
    int SuccessCount,
    int FailureCount,
    IReadOnlyDictionary<int, string> ErrorsByRepositoryId)
{
    public static WorkspaceBranchBulkResult Empty { get; } = new(0, 0, new Dictionary<int, string>());
}

