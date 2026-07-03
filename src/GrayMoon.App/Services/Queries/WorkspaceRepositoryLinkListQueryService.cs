using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Queries;

public sealed class WorkspaceRepositoryLinkListQueryService(AppDbContext dbContext) : IWorkspaceRepositoryLinkListQueryService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<int> CountAsync(WorkspaceRepositoryLinkListFilter filter, CancellationToken cancellationToken = default) =>
        ApplyFilters(_dbContext.WorkspaceRepositories.AsNoTracking(), filter).CountAsync(cancellationToken);

    public async Task<WorkspaceRepositoryLinkListPageResult> GetPageAsync(
        WorkspaceRepositoryLinkListRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = new WorkspaceRepositoryLinkListFilter(request.WorkspaceId, request.Search);
        var query = ApplyFilters(_dbContext.WorkspaceRepositories.AsNoTracking(), filter);
        query = ApplySort(query);
        query = ApplyKeyset(query, request.Cursor);

        var take = Math.Max(1, request.PageSize) + 1;
        var rows = await Project(query).Take(take).ToListAsync(cancellationToken);

        var hasMore = rows.Count > request.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        WorkspaceRepositoryLinkListCursor? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = ToCursor(last);
        }

        return new WorkspaceRepositoryLinkListPageResult(rows, nextCursor, hasMore);
    }

    public async Task<WorkspaceRepositoryHeaderStateDto> GetHeaderStateAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var links = await _dbContext.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new
            {
                wr.DependencyLevel,
                IsOnTag = wr.CheckedOutTag != null && wr.CheckedOutTag != string.Empty,
                wr.UnmatchedDeps,
                wr.OutOfDateFileRepos,
                wr.OutgoingCommits,
                wr.BranchHasUpstream,
                wr.IncomingCommits,
                wr.BranchName,
                wr.DefaultBranchName,
                wr.SyncStatus,
            })
            .ToListAsync(cancellationToken);

        var totalCount = links.Count;
        var hasUnmatchedDependencies = links.Any(wr => !wr.IsOnTag
            && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0));
        var isPushRecommended = links.Any(wr => !wr.IsOnTag
            && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false));
        var hasIncomingCommits = links.Any(wr => !wr.IsOnTag
            && (wr.IncomingCommits ?? 0) > 0
            && !string.IsNullOrWhiteSpace(wr.BranchName)
            && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
            && wr.BranchName == wr.DefaultBranchName);
        var hasTaggedRepos = links.Any(wr => wr.IsOnTag);
        var isOutOfSync = links.Any(wr => wr.SyncStatus != RepoSyncStatus.InSync);
        var lowestLevelNeedingWork = links
            .Where(wr => !wr.IsOnTag && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0))
            .Select(wr => wr.DependencyLevel)
            .Min();

        return new WorkspaceRepositoryHeaderStateDto(
            totalCount,
            hasUnmatchedDependencies,
            isPushRecommended,
            hasIncomingCommits,
            hasTaggedRepos,
            isOutOfSync,
            lowestLevelNeedingWork);
    }

    public async Task<IReadOnlyList<int>> GetRepositoryIdsAtLevelAsync(
        int workspaceId,
        int? levelKey,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, search);
        var query = ApplyFilters(_dbContext.WorkspaceRepositories.AsNoTracking(), filter)
            .Where(wr => wr.DependencyLevel == levelKey);

        return await query
            .OrderBy(wr => wr.WorkspaceRepositoryId)
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetAllSnapshotsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId);
        query = ApplySort(query);
        return await Project(query).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetGitVersionNameMapAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId
                && wr.Repository != null
                && wr.GitVersion != null
                && wr.GitVersion != string.Empty)
            .Select(wr => new { wr.Repository!.RepositoryName, wr.GitVersion })
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.RepositoryName) && !string.IsNullOrEmpty(r.GitVersion))
            .ToDictionary(r => r.RepositoryName!, r => r.GitVersion!, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WorkspaceRepositoryLinkListItemDto?> GetSnapshotAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        return await Project(_dbContext.WorkspaceRepositories.AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<WorkspaceRepositoryLink> ApplyFilters(
        IQueryable<WorkspaceRepositoryLink> query,
        WorkspaceRepositoryLinkListFilter filter)
    {
        query = query.Where(wr => wr.WorkspaceId == filter.WorkspaceId);
        return query.ApplySearch(filter.Search, WorkspaceRepositoryLinkSearchExpressions.BuildTermPredicate);
    }

    private static IQueryable<WorkspaceRepositoryLink> ApplySort(IQueryable<WorkspaceRepositoryLink> query) =>
        query
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenBy(wr => wr.RepositoryType == ProjectType.Service ? 0
                : wr.RepositoryType == ProjectType.Package ? 1
                : wr.RepositoryType == ProjectType.Executable ? 2
                : wr.RepositoryType == ProjectType.Library ? 3
                : wr.RepositoryType == ProjectType.Test ? 4
                : 5)
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ThenBy(wr => wr.WorkspaceRepositoryId);

    private static IQueryable<WorkspaceRepositoryLink> ApplyKeyset(
        IQueryable<WorkspaceRepositoryLink> query,
        WorkspaceRepositoryLinkListCursor? cursor)
    {
        if (cursor is null)
        {
            return query;
        }

        return query.Where(wr =>
            (wr.DependencyLevel ?? int.MinValue) < cursor.DependencyLevelSortKey
            || ((wr.DependencyLevel ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (wr.RepositoryType == ProjectType.Service ? 0
                    : wr.RepositoryType == ProjectType.Package ? 1
                    : wr.RepositoryType == ProjectType.Executable ? 2
                    : wr.RepositoryType == ProjectType.Library ? 3
                    : wr.RepositoryType == ProjectType.Test ? 4
                    : 5) > cursor.RepositoryTypeSortKey)
            || ((wr.DependencyLevel ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (wr.RepositoryType == ProjectType.Service ? 0
                    : wr.RepositoryType == ProjectType.Package ? 1
                    : wr.RepositoryType == ProjectType.Executable ? 2
                    : wr.RepositoryType == ProjectType.Library ? 3
                    : wr.RepositoryType == ProjectType.Test ? 4
                    : 5) == cursor.RepositoryTypeSortKey
                && (wr.Dependencies ?? int.MinValue) < cursor.DependenciesSortKey)
            || ((wr.DependencyLevel ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (wr.RepositoryType == ProjectType.Service ? 0
                    : wr.RepositoryType == ProjectType.Package ? 1
                    : wr.RepositoryType == ProjectType.Executable ? 2
                    : wr.RepositoryType == ProjectType.Library ? 3
                    : wr.RepositoryType == ProjectType.Test ? 4
                    : 5) == cursor.RepositoryTypeSortKey
                && (wr.Dependencies ?? int.MinValue) == cursor.DependenciesSortKey
                && wr.WorkspaceRepositoryId > cursor.WorkspaceRepositoryId));
    }

    private static IQueryable<WorkspaceRepositoryLinkListItemDto> Project(IQueryable<WorkspaceRepositoryLink> query) =>
        query.Select(wr => new WorkspaceRepositoryLinkListItemDto(
            wr.WorkspaceRepositoryId,
            wr.WorkspaceId,
            wr.RepositoryId,
            wr.Repository != null ? wr.Repository.RepositoryName : string.Empty,
            wr.Repository != null ? wr.Repository.CloneUrl : string.Empty,
            wr.GitVersion,
            wr.BranchName,
            wr.CheckedOutTag,
            wr.DefaultBranchName,
            wr.OutgoingCommits,
            wr.IncomingCommits,
            wr.DefaultBranchBehindCommits,
            wr.DefaultBranchAheadCommits,
            wr.BranchHasUpstream,
            wr.SyncStatus,
            wr.DependencyLevel,
            wr.Dependencies,
            wr.UnmatchedDeps,
            wr.OutOfDateFileRepos,
            wr.RepositoryType,
            wr.HasNewerTag,
            wr.PullRequest != null ? wr.PullRequest.State : null,
            wr.PullRequest != null ? wr.PullRequest.PullRequestNumber : null,
            wr.PullRequest != null ? wr.PullRequest.HtmlUrl : null,
            wr.PullRequest != null ? wr.PullRequest.MergedAt : null,
            wr.PullRequest != null ? wr.PullRequest.Mergeable : null,
            wr.PullRequest != null ? wr.PullRequest.MergeableState : null,
            wr.PullRequest != null ? wr.PullRequest.ChangedFiles : null));

    private static WorkspaceRepositoryLinkListCursor ToCursor(WorkspaceRepositoryLinkListItemDto dto) =>
        new(
            dto.DependencyLevel ?? int.MinValue,
            WorkspaceRepositoryLinkSearchExpressions.GetRepositoryTypeSortKey(dto.RepositoryType),
            dto.Dependencies ?? int.MinValue,
            dto.WorkspaceRepositoryId);
}
