namespace GrayMoon.App.Services.Queries;

public interface IWorkspaceProjectListQueryService
{
    const int DefaultChunkSize = 50;

    Task<int> CountAsync(WorkspaceProjectListFilter filter, CancellationToken cancellationToken = default);

    Task<WorkspaceProjectListPageResult> GetPageAsync(WorkspaceProjectListRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetIndexAsync(WorkspaceProjectListFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceProjectListItemDto>> GetByIdsAsync(
        IReadOnlyList<int> projectIds,
        CancellationToken cancellationToken = default);
}
