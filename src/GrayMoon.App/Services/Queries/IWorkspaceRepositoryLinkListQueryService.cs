using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Queries;

public interface IWorkspaceRepositoryLinkListQueryService
{
    const int DefaultChunkSize = 50;

    Task<int> CountAsync(WorkspaceRepositoryLinkListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="contextId"/>/<paramref name="isSpecialWorkspace"/> select which context's checkout/PR/
    /// dependency state is overlaid onto the returned rows. Omit them (or pass <paramref name="isSpecialWorkspace"/>
    /// true) to read the special Workspace's state straight off the shared link, matching legacy behavior.
    /// </summary>
    Task<WorkspaceRepositoryLinkListPageResult> GetPageAsync(
        WorkspaceRepositoryLinkListRequest request,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryHeaderStateDto> GetHeaderStateAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default);

    /// <summary>Ordered lightweight index for virtual scroll (same sort as the grid).</summary>
    Task<IReadOnlyList<WorkspaceRepositoryLinkIndexEntry>> GetIndexAsync(
        WorkspaceRepositoryLinkListFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Hydrates full list DTOs for the given workspace-repository link PKs.</summary>
    Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetByIdsAsync(
        int workspaceId,
        IReadOnlyList<int> workspaceRepositoryIds,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetRepositoryIdsAtLevelAsync(
        int workspaceId,
        int? levelKey,
        string? search,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetAllSnapshotsAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetGitVersionNameMapAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRepositoryLinkListItemDto?> GetSnapshotAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default);
}
