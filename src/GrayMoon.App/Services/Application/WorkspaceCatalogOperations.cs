using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services.Application;

public sealed record WorkspaceCatalogItem(
    int WorkspaceId,
    string Name,
    bool IsDefault,
    bool IsInSync,
    DateTime? LastSyncedAt,
    int RepositoryCount);

public interface IWorkspaceCatalogOperations
{
    Task<IReadOnlyList<WorkspaceCatalogItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceCatalogItem?> GetAsync(int workspaceId, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceCatalogOperations(WorkspaceRepository workspaceRepository) : IWorkspaceCatalogOperations
{
    public async Task<IReadOnlyList<WorkspaceCatalogItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var workspaces = await workspaceRepository.GetAllAsync();
        return workspaces.Select(ToItem).ToList();
    }

    public async Task<WorkspaceCatalogItem?> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        return workspace == null ? null : ToItem(workspace);
    }

    private static WorkspaceCatalogItem ToItem(Models.Workspace workspace)
        => new(
            workspace.WorkspaceId,
            workspace.Name,
            workspace.IsDefault,
            workspace.IsInSync,
            workspace.LastSyncedAt,
            workspace.Repositories?.Count ?? 0);
}
