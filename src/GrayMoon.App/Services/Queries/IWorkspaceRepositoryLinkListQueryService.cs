namespace GrayMoon.App.Services.Queries;

public interface IWorkspaceRepositoryLinkListQueryService
{
    const int DefaultChunkSize = 50;

    Task<int> CountAsync(WorkspaceRepositoryLinkListFilter filter, CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryLinkListPageResult> GetPageAsync(
        WorkspaceRepositoryLinkListRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryHeaderStateDto> GetHeaderStateAsync(int workspaceId, CancellationToken cancellationToken = default);

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
