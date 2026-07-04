using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Queries;

public sealed class WorkspaceProjectListQueryService(AppDbContext dbContext) : IWorkspaceProjectListQueryService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<int> CountAsync(WorkspaceProjectListFilter filter, CancellationToken cancellationToken = default) =>
        ApplyFilters(_dbContext.WorkspaceProjects.AsNoTracking(), filter).CountAsync(cancellationToken);

    public async Task<WorkspaceProjectListPageResult> GetPageAsync(
        WorkspaceProjectListRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = new WorkspaceProjectListFilter(request.WorkspaceId, request.Search);
        var query = ApplyFilters(_dbContext.WorkspaceProjects.AsNoTracking(), filter);
        query = ApplySort(query);
        query = ApplyKeyset(query, request.Cursor);

        var take = Math.Max(1, request.PageSize) + 1;
        var rows = await query
            .Select(p => new WorkspaceProjectListItemDto(
                p.ProjectId,
                p.ProjectName,
                p.ProjectType,
                p.TargetFramework,
                p.ProjectFilePath))
            .Take(take)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > request.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        WorkspaceProjectListCursor? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = new WorkspaceProjectListCursor(
                WorkspaceProjectSearchExpressions.GetProjectTypeSortKey(last.ProjectType),
                last.ProjectName,
                last.ProjectId);
        }

        return new WorkspaceProjectListPageResult(rows, nextCursor, hasMore);
    }

    public async Task<IReadOnlyList<int>> GetIndexAsync(
        WorkspaceProjectListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyFilters(_dbContext.WorkspaceProjects.AsNoTracking(), filter);
        query = ApplySort(query);
        return await query
            .Select(p => p.ProjectId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceProjectListItemDto>> GetByIdsAsync(
        IReadOnlyList<int> projectIds,
        CancellationToken cancellationToken = default)
    {
        if (projectIds.Count == 0)
        {
            return Array.Empty<WorkspaceProjectListItemDto>();
        }

        var idSet = projectIds.ToHashSet();
        var rows = await _dbContext.WorkspaceProjects.AsNoTracking()
            .Where(p => idSet.Contains(p.ProjectId))
            .Select(p => new WorkspaceProjectListItemDto(
                p.ProjectId,
                p.ProjectName,
                p.ProjectType,
                p.TargetFramework,
                p.ProjectFilePath))
            .ToListAsync(cancellationToken);

        var order = projectIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        return rows
            .OrderBy(r => order.GetValueOrDefault(r.ProjectId, int.MaxValue))
            .ToList();
    }

    private static IQueryable<WorkspaceProject> ApplyFilters(IQueryable<WorkspaceProject> query, WorkspaceProjectListFilter filter)
    {
        query = query.Where(p => p.WorkspaceId == filter.WorkspaceId);
        return query.ApplySearch(filter.Search, WorkspaceProjectSearchExpressions.BuildTermPredicate);
    }

    private static IQueryable<WorkspaceProject> ApplySort(IQueryable<WorkspaceProject> query) =>
        query
            .OrderBy(p => p.ProjectType == ProjectType.Service ? 0
                : p.ProjectType == ProjectType.Library ? 1
                : p.ProjectType == ProjectType.Package ? 2
                : p.ProjectType == ProjectType.Test ? 3
                : 4)
            .ThenBy(p => p.ProjectName)
            .ThenBy(p => p.ProjectId);

    private static IQueryable<WorkspaceProject> ApplyKeyset(IQueryable<WorkspaceProject> query, WorkspaceProjectListCursor? cursor)
    {
        if (cursor is null)
        {
            return query;
        }

        return query.Where(p =>
            (p.ProjectType == ProjectType.Service ? 0
                : p.ProjectType == ProjectType.Library ? 1
                : p.ProjectType == ProjectType.Package ? 2
                : p.ProjectType == ProjectType.Test ? 3
                : 4) > cursor.ProjectTypeSortKey
            || ((p.ProjectType == ProjectType.Service ? 0
                    : p.ProjectType == ProjectType.Library ? 1
                    : p.ProjectType == ProjectType.Package ? 2
                    : p.ProjectType == ProjectType.Test ? 3
                    : 4) == cursor.ProjectTypeSortKey
                && p.ProjectName.CompareTo(cursor.ProjectName) > 0)
            || ((p.ProjectType == ProjectType.Service ? 0
                    : p.ProjectType == ProjectType.Library ? 1
                    : p.ProjectType == ProjectType.Package ? 2
                    : p.ProjectType == ProjectType.Test ? 3
                    : 4) == cursor.ProjectTypeSortKey
                && p.ProjectName == cursor.ProjectName
                && p.ProjectId > cursor.ProjectId));
    }
}
