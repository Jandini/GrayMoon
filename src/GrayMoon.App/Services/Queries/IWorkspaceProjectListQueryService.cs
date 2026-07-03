namespace GrayMoon.App.Services.Queries;

public interface IWorkspaceProjectListQueryService
{
    const int DefaultChunkSize = 50;

    Task<int> CountAsync(WorkspaceProjectListFilter filter, CancellationToken cancellationToken = default);

    Task<WorkspaceProjectListPageResult> GetPageAsync(WorkspaceProjectListRequest request, CancellationToken cancellationToken = default);
}
