namespace GrayMoon.Application;

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
