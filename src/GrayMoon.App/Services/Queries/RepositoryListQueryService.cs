using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Queries;

public sealed class RepositoryListQueryService(AppDbContext dbContext) : IRepositoryListQueryService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Repositories.AsNoTracking().AnyAsync(cancellationToken);

    public Task<int> CountAsync(RepositoryListFilter filter, CancellationToken cancellationToken = default) =>
        ApplyFilters(_dbContext.Repositories.AsNoTracking(), filter).CountAsync(cancellationToken);

    public async Task<RepositoryListPageResult> GetPageAsync(RepositoryListRequest request, CancellationToken cancellationToken = default)
    {
        var filter = new RepositoryListFilter(
            request.Search,
            request.RestrictToRepositoryIds,
            request.SortField,
            request.SortDescending);

        var query = ApplyFilters(_dbContext.Repositories.AsNoTracking(), filter);
        query = ApplySort(query, request.SortField, request.SortDescending);
        query = ApplyKeyset(query, request.Cursor, request.SortDescending);

        var take = Math.Max(1, request.PageSize) + 1;
        var rows = await query
            .Select(r => new RepositoryListItemDto(
                r.RepositoryId,
                r.RepositoryName,
                r.OrgName,
                r.Connector != null ? r.Connector.ConnectorName : "Unknown",
                r.Visibility,
                r.Archived,
                r.Topics))
            .Take(take)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > request.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        RepositoryListCursor? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = new RepositoryListCursor(last.RepositoryName, last.RepositoryId);
        }

        return new RepositoryListPageResult(rows, nextCursor, hasMore);
    }

    public async Task<IReadOnlyList<int>> GetMatchingIdsAsync(RepositoryListFilter filter, CancellationToken cancellationToken = default)
    {
        return await ApplyFilters(_dbContext.Repositories.AsNoTracking(), filter)
            .OrderBy(r => r.RepositoryName)
            .ThenBy(r => r.RepositoryId)
            .Select(r => r.RepositoryId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> FilterExistingIdsAsync(IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.Repositories.AsNoTracking()
            .Where(r => repositoryIds.Contains(r.RepositoryId))
            .Select(r => r.RepositoryId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetIdsByCloneUrlsAsync(IReadOnlyList<string> cloneUrls, CancellationToken cancellationToken = default)
    {
        if (cloneUrls.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = cloneUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await _dbContext.Repositories.AsNoTracking()
            .Where(r => normalized.Contains(r.CloneUrl))
            .Select(r => new { r.CloneUrl, r.RepositoryId })
            .ToListAsync(cancellationToken);

        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var url = row.CloneUrl.Trim();
            if (!dict.ContainsKey(url))
            {
                dict[url] = row.RepositoryId;
            }
        }

        return dict;
    }

    private static IQueryable<Repository> ApplyFilters(IQueryable<Repository> query, RepositoryListFilter filter)
    {
        query = query.ApplySearch(filter.Search, RepositorySearchExpressions.BuildTermPredicate);

        if (filter.RestrictToRepositoryIds is { Count: > 0 } ids)
        {
            query = query.Where(r => ids.Contains(r.RepositoryId));
        }

        return query;
    }

    private static IQueryable<Repository> ApplySort(IQueryable<Repository> query, RepositorySortField sortField, bool descending) =>
        sortField switch
        {
            RepositorySortField.Name when descending => query
                .OrderByDescending(r => r.RepositoryName)
                .ThenByDescending(r => r.RepositoryId),
            RepositorySortField.Name => query
                .OrderBy(r => r.RepositoryName)
                .ThenBy(r => r.RepositoryId),
            _ => query.OrderBy(r => r.RepositoryName).ThenBy(r => r.RepositoryId),
        };

    private static IQueryable<Repository> ApplyKeyset(
        IQueryable<Repository> query,
        RepositoryListCursor? cursor,
        bool descending)
    {
        if (cursor is null)
        {
            return query;
        }

        if (descending)
        {
            return query.Where(r =>
                r.RepositoryName.CompareTo(cursor.RepositoryName) < 0
                || (r.RepositoryName == cursor.RepositoryName && r.RepositoryId < cursor.RepositoryId));
        }

        return query.Where(r =>
            r.RepositoryName.CompareTo(cursor.RepositoryName) > 0
            || (r.RepositoryName == cursor.RepositoryName && r.RepositoryId > cursor.RepositoryId));
    }
}
