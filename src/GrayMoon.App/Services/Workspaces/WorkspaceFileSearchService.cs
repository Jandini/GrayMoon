using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>Runs file search via the worker for a workspace. Used by AddFilesModal.</summary>
public interface IWorkspaceFileSearchService
{
    Task<WorkerSearchFilesResponse?> SearchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? pattern,
        string? repositoryName,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceFileSearchService(
    IWorkerBridge workerBridge,
    WorkspaceRepository workspaceRepository,
    IWorkspaceContextPathResolver pathResolver) : IWorkspaceFileSearchService
{
    public async Task<WorkerSearchFilesResponse?> SearchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? pattern,
        string? repositoryName,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null || !workerBridge.IsWorkerConnected)
            return null;

        var (workspaceRoot, workspaceFolderName) = await pathResolver.GetWorkerWorkspaceArgsAsync(contextId, cancellationToken);
        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var response = await workerBridge.SendCommandAsync("SearchFiles", new
        {
            workspaceName = workspaceFolderName,
            repositoryName = string.IsNullOrWhiteSpace(repositoryName) ? null : repositoryName.Trim(),
            searchPattern,
            workspaceRoot
        }, cancellationToken);

        if (!response.Success || response.Data == null)
            return null;

        return WorkerResponseJson.DeserializeWorkerResponse<WorkerSearchFilesResponse>(response.Data)
            ?? new WorkerSearchFilesResponse { Files = [] };
    }
}