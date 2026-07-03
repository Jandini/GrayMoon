namespace GrayMoon.App.Services.Queries;

public enum RepositorySortField
{
    Name,
}

public sealed record RepositoryListCursor(string RepositoryName, int RepositoryId);

public sealed record RepositoryListRequest(
    string? Search,
    IReadOnlyList<int>? RestrictToRepositoryIds,
    RepositorySortField SortField,
    bool SortDescending,
    int PageSize,
    RepositoryListCursor? Cursor);

public sealed record RepositoryListItemDto(
    int RepositoryId,
    string RepositoryName,
    string? OrgName,
    string ConnectorName,
    string Visibility,
    bool Archived,
    string? Topics);

public sealed record RepositoryListPageResult(
    IReadOnlyList<RepositoryListItemDto> Items,
    RepositoryListCursor? NextCursor,
    bool HasMore);

public sealed record RepositoryListFilter(
    string? Search,
    IReadOnlyList<int>? RestrictToRepositoryIds,
    RepositorySortField SortField = RepositorySortField.Name,
    bool SortDescending = false);
