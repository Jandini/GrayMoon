namespace GrayMoon.App.Services.Queries;

public interface IWorkspaceRepositoryLinkListQueryService
{
    const int DefaultChunkSize = 50;

    Task<int> CountAsync(WorkspaceRepositoryLinkListFilter filter, CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryLinkListPageResult> GetPageAsync(
        WorkspaceRepositoryLinkListRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryHeaderStateDto> GetHeaderStateAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Ordered lightweight index for virtual scroll (same sort as the grid).</summary>
    Task<IReadOnlyList<WorkspaceRepositoryLinkIndexEntry>> GetIndexAsync(
        WorkspaceRepositoryLinkListFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Hydrates full list DTOs for the given workspace-repository link PKs.</summary>
    Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetByIdsAsync(
        int workspaceId,
        IReadOnlyList<int> workspaceRepositoryIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetRepositoryIdsAtLevelAsync(
        int workspaceId,
        int? levelKey,
        string? search,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetAllSnapshotsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetGitVersionNameMapAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryLinkListItemDto?> GetSnapshotAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken = default);
}
