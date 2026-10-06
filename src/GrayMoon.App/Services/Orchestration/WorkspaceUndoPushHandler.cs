using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.Options;

using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Orchestration;

public sealed class WorkspaceUndoPushHandler(
    IWorkerBridge workerBridge,
    WorkspaceRepository workspaceRepo,
    IOptions<WorkspaceOptions> options,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    ILogger<WorkspaceUndoPushHandler> logger)
{
    public async Task<IReadOnlyList<(int RepositoryId, bool Success, string? Error)>> RunUndoPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<WorkspaceRepositoryLink> repos,
        bool keepChanges,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var targets = repos
            .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0 && !string.IsNullOrWhiteSpace(wr.BranchName))
            .ToList();

        if (targets.Count == 0)
            return Array.Empty<(int, bool, string?)>();

        var workspace = await workspaceRepo.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Array.Empty<(int, bool, string?)>();

        var (workspaceRoot, workspaceFolderName) = await pathResolver.GetWorkerWorkspaceArgsAsync(contextId, ct);
        var capabilities = (await capabilitiesResolver.GetAsync(workspaceId, ct)).ToRepositoryOperationCapabilities();

        var total = targets.Count;
        var completedCount = 0;
        var maxParallel = Math.Max(1, options.Value?.MaxParallelOperations ?? 16);

        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

        var tasks = targets.Select(async wr =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var args = new
                {
                    workspaceName = workspaceFolderName,
                    repositoryId = wr.RepositoryId,
                    repositoryName = wr.Repository?.RepositoryName,
                    workspaceId,
                    branchName = wr.BranchName,
                    keepChanges,
                    workspaceRoot,
                    bearerToken = ConnectorHelpers.UnprotectToken(wr.Repository?.Connector?.UserToken),
                    capabilities,
                };
                var response = await workerBridge.SendCommandAsync("UndoPush", args, ct);
                if (!response.Success || response.Data == null)
                {
                    var errMsg = response.Error ?? "Worker command failed";
                    logger.LogError("UndoPush failed for repo {RepositoryId}: {Error}", wr.RepositoryId, errMsg);
                    return (wr.RepositoryId, false, errMsg);
                }

                var result = WorkerResponseJson.DeserializeWorkerResponse<UndoPushWorkerResponse>(response.Data);
                if (result is not { Success: true })
                {
                    var errMsg = result?.ErrorMessage ?? "Unknown error";
                    logger.LogError("UndoPush worker returned failure for repo {RepositoryId}: {Error}", wr.RepositoryId, errMsg);
                    return (wr.RepositoryId, false, errMsg);
                }

                return (wr.RepositoryId, true, (string?)null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UndoPush exception for repo {RepositoryId}", wr.RepositoryId);
                return (wr.RepositoryId, false, ex.Message);
            }
            finally
            {
                semaphore.Release();
                var c = Interlocked.Increment(ref completedCount);
                if (total > 1)
                    progress.Report($"Reverting {c} of {total} repositories...", c, total);
            }
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private sealed class UndoPushWorkerResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
