using System.Text.Json;
using GrayMoon.App.Models.Api;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles commit-sync (pull) operations for a workspace.
/// Stateless; all state is provided by the caller via parameters and callbacks.
/// </summary>
public sealed class WorkspaceCommitSyncHandler(ILogger<WorkspaceCommitSyncHandler> logger, IHttpClientFactory httpClientFactory)
{
    public async Task CommitSyncAsync(
        int workspaceId,
        int repositoryId,
        string apiBaseUrl,
        CancellationToken cancellationToken,
        Func<string, Task> setProgress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        await setProgress("Synchronizing commits...");

        var httpClient = httpClientFactory.CreateClient();
        var request = new
        {
            repositoryId,
            workspaceId
        };
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{apiBaseUrl}/api/commitsync", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<CommitSyncResponse>(responseContent, AgentResponseJson.Options);

            if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                setRepositoryError(repositoryId, result.ErrorMessage);
                await setProgress(result.MergeConflict ? "Merge conflict detected. Merge aborted." : "Commit sync completed with errors.");
            }
            else if (result != null && result.MergeConflict)
            {
                await setProgress("Merge conflict detected. Merge aborted.");
            }
            else
            {
                setRepositoryError(repositoryId, null);
            }
        }
        else
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            var displayError = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText) ?? $"Commit sync failed: {response.StatusCode}";
            setRepositoryError(repositoryId, displayError);
            setPageError(displayError);
            logger.LogError("CommitSync failed: {StatusCode}, {Error}", response.StatusCode, errorText);
        }
    }

    public async Task CommitSyncLevelAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        string apiBaseUrl,
        CancellationToken cancellationToken,
        Func<int, int, Task> reportProgress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        if (repositoryIds.Count == 0)
            return;

        var httpClient = httpClientFactory.CreateClient();
        var baseUrl = apiBaseUrl;
        var total = repositoryIds.Count;
        var completedCount = 0;

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            try
            {
                var request = new
                {
                    repositoryId,
                    workspaceId
                };
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync($"{baseUrl}/api/commitsync", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = JsonSerializer.Deserialize<CommitSyncResponse>(responseContent, AgentResponseJson.Options);

                    if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        setRepositoryError(repositoryId, result.ErrorMessage);
                    }
                    else if (result != null && result.MergeConflict)
                    {
                        setRepositoryError(repositoryId, "Merge conflict detected. Merge aborted.");
                    }
                    else
                    {
                        setRepositoryError(repositoryId, null);
                    }
                }
                else
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    var displayError = ApiErrorHelper.TryGetErrorMessageFromResponseBody(errorText) ?? $"Commit sync failed: {response.StatusCode}";
                    setRepositoryError(repositoryId, displayError);
                    logger.LogError("CommitSync failed for repository {RepositoryId}: {StatusCode}, {Error}", repositoryId, response.StatusCode, errorText);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error syncing commits for repository {RepositoryId}", repositoryId);
                setRepositoryError(repositoryId, "Commit sync failed. The GrayMoon Agent may be offline.");
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedCount);
                await reportProgress(completed, total);
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Caller handles reload on cancel.
        }
    }
}

