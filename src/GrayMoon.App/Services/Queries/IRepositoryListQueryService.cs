namespace GrayMoon.App.Services.Queries;

public interface IRepositoryListQueryService
{
    const int DefaultChunkSize = 50;

    Task<bool> AnyAsync(CancellationToken cancellationToken = default);

    Task<int> CountAsync(RepositoryListFilter filter, CancellationToken cancellationToken = default);

    Task<RepositoryListPageResult> GetPageAsync(RepositoryListRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetMatchingIdsAsync(RepositoryListFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> FilterExistingIdsAsync(IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, int>> GetIdsByCloneUrlsAsync(IReadOnlyList<string> cloneUrls, CancellationToken cancellationToken = default);
}
