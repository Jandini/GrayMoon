using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Search;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Queries;

/// <summary>
/// Link-list queries use a factory so each call owns a short-lived DbContext.
/// That avoids concurrent-use races when Blazor circuits share work across Job.Run, scroll, and tooltips.
/// </summary>
public sealed class WorkspaceRepositoryLinkListQueryService(IDbContextFactory<AppDbContext> dbContextFactory)
    : IWorkspaceRepositoryLinkListQueryService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    public async Task<int> CountAsync(WorkspaceRepositoryLinkListFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ApplyFilters(db.WorkspaceRepositories.AsNoTracking(), filter).CountAsync(cancellationToken);
    }

    public async Task<WorkspaceRepositoryLinkListPageResult> GetPageAsync(
        WorkspaceRepositoryLinkListRequest request,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var filter = new WorkspaceRepositoryLinkListFilter(request.WorkspaceId, request.Search);
        var query = ApplyFilters(db.WorkspaceRepositories.AsNoTracking(), filter);
        query = ApplySort(query, db, contextId, isSpecialWorkspace);
        query = ApplyKeyset(query, request.Cursor, db, contextId, isSpecialWorkspace);

        var take = Math.Max(1, request.PageSize) + 1;
        var rows = await Project(query, db, contextId, isSpecialWorkspace).Take(take).ToListAsync(cancellationToken);

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
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var linkQuery = db.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId);

        var totalCount = await linkQuery.CountAsync(cancellationToken);

        if (isSpecialWorkspace || contextId is null)
        {
            var hasUnmatchedDependenciesLegacy = await linkQuery.AnyAsync(
                wr => (wr.CheckedOutTag == null || wr.CheckedOutTag == string.Empty)
                    && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0),
                cancellationToken);

            var isPushRecommendedLegacy = await linkQuery.AnyAsync(
                wr => (wr.CheckedOutTag == null || wr.CheckedOutTag == string.Empty)
                    && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false),
                cancellationToken);

            var hasIncomingCommitsLegacy = await linkQuery.AnyAsync(
                wr => (wr.CheckedOutTag == null || wr.CheckedOutTag == string.Empty)
                    && (wr.IncomingCommits ?? 0) > 0,
                cancellationToken);

            var hasTaggedReposLegacy = await linkQuery.AnyAsync(
                wr => wr.CheckedOutTag != null && wr.CheckedOutTag != string.Empty,
                cancellationToken);

            var isOutOfSyncLegacy = await linkQuery.AnyAsync(
                wr => wr.SyncStatus != RepoSyncStatus.InSync,
                cancellationToken);

            var lowestLevelsLegacy = await linkQuery
                .Where(wr => (wr.CheckedOutTag == null || wr.CheckedOutTag == string.Empty)
                    && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0)
                    && wr.DependencyLevel != null)
                .Select(wr => wr.DependencyLevel!.Value)
                .OrderBy(level => level)
                .Take(1)
                .ToListAsync(cancellationToken);
            int? lowestLevelNeedingWorkLegacy = lowestLevelsLegacy.Count > 0 ? lowestLevelsLegacy[0] : null;

            // Same eligibility as PRBadge.ShowsCreateBadge once PR state is persisted: ahead of default,
            // not on a tag, and no open/merged/closed pull request.
            var hasCreatablePrLegacy = await linkQuery.AnyAsync(
                wr => (wr.CheckedOutTag == null || wr.CheckedOutTag == string.Empty)
                    && (wr.DefaultBranchAheadCommits ?? 0) > 0
                    && (wr.PullRequest == null
                        || (wr.PullRequest.MergedAt == null
                            && wr.PullRequest.State != "open"
                            && wr.PullRequest.State != "closed")),
                cancellationToken);

            return new WorkspaceRepositoryHeaderStateDto(
                totalCount,
                hasUnmatchedDependenciesLegacy,
                isPushRecommendedLegacy,
                hasIncomingCommitsLegacy,
                hasTaggedReposLegacy,
                isOutOfSyncLegacy,
                lowestLevelNeedingWorkLegacy,
                hasCreatablePrLegacy);
        }

        var cid = contextId.Value.Value;
        var stateQuery =
            from wr in linkQuery
            join s in db.WorkspaceRepositoryContextStates.AsNoTracking().Where(s => s.WorkspaceFeatureContextId == cid)
                on wr.WorkspaceRepositoryId equals s.WorkspaceRepositoryId into states
            from state in states.DefaultIfEmpty()
            select new { wr, state };

        var hasUnmatchedDependencies = await stateQuery.AnyAsync(
            x => (x.state == null || string.IsNullOrEmpty(x.state.CheckedOutTag))
                && x.state != null
                && ((x.state.UnmatchedDeps ?? 0) > 0 || (x.state.OutOfDateFileRepos ?? 0) > 0),
            cancellationToken);

        var isPushRecommended = await stateQuery.AnyAsync(
            x => x.state != null
                && string.IsNullOrEmpty(x.state.CheckedOutTag)
                && ((x.state.OutgoingCommits ?? 0) > 0 || x.state.BranchHasUpstream == false),
            cancellationToken);

        var hasIncomingCommits = await stateQuery.AnyAsync(
            x => x.state != null
                && string.IsNullOrEmpty(x.state.CheckedOutTag)
                && (x.state.IncomingCommits ?? 0) > 0,
            cancellationToken);

        var hasTaggedRepos = await stateQuery.AnyAsync(
            x => x.state != null && !string.IsNullOrEmpty(x.state.CheckedOutTag),
            cancellationToken);

        var isOutOfSync = await stateQuery.AnyAsync(
            x => x.state == null || x.state.SyncStatus != RepoSyncStatus.InSync,
            cancellationToken);

        var lowestLevels = await stateQuery
            .Where(x => x.state != null
                && string.IsNullOrEmpty(x.state.CheckedOutTag)
                && ((x.state.UnmatchedDeps ?? 0) > 0 || (x.state.OutOfDateFileRepos ?? 0) > 0)
                && x.state.DependencyLevel != null)
            .Select(x => x.state!.DependencyLevel!.Value)
            .OrderBy(level => level)
            .Take(1)
            .ToListAsync(cancellationToken);
        int? lowestLevelNeedingWork = lowestLevels.Count > 0 ? lowestLevels[0] : null;

        var prQuery =
            from x in stateQuery
            join pr in db.WorkspaceRepositoryContextPullRequests.AsNoTracking().Where(p => p.WorkspaceFeatureContextId == cid)
                on x.wr.WorkspaceRepositoryId equals pr.WorkspaceRepositoryId into prs
            from pr in prs.DefaultIfEmpty()
            select new { x.state, pr };

        var hasCreatablePr = await prQuery.AnyAsync(
            x => x.state != null
                && string.IsNullOrEmpty(x.state.CheckedOutTag)
                && (x.state.DefaultBranchAheadCommits ?? 0) > 0
                && (x.pr == null
                    || (x.pr.MergedAt == null && x.pr.State != "open" && x.pr.State != "closed")),
            cancellationToken);

        return new WorkspaceRepositoryHeaderStateDto(
            totalCount,
            hasUnmatchedDependencies,
            isPushRecommended,
            hasIncomingCommits,
            hasTaggedRepos,
            isOutOfSync,
            lowestLevelNeedingWork,
            hasCreatablePr);
    }

    public async Task<IReadOnlyList<WorkspaceRepositoryLinkIndexEntry>> GetIndexAsync(
        WorkspaceRepositoryLinkListFilter filter,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = ApplyFilters(db.WorkspaceRepositories.AsNoTracking(), filter);
        query = ApplySort(query, db, contextId, isSpecialWorkspace);

        if (isSpecialWorkspace || contextId is null)
        {
            return await query
                .Select(wr => new WorkspaceRepositoryLinkIndexEntry(
                    wr.WorkspaceRepositoryId,
                    wr.RepositoryId,
                    wr.DependencyLevel))
                .ToListAsync(cancellationToken);
        }

        var cid = contextId.Value.Value;
        return await query
            .Select(wr => new WorkspaceRepositoryLinkIndexEntry(
                wr.WorkspaceRepositoryId,
                wr.RepositoryId,
                db.WorkspaceRepositoryContextStates
                    .Where(s => s.WorkspaceFeatureContextId == cid && s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                    .Select(s => s.DependencyLevel)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetByIdsAsync(
        int workspaceId,
        IReadOnlyList<int> workspaceRepositoryIds,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        if (workspaceRepositoryIds.Count == 0)
        {
            return Array.Empty<WorkspaceRepositoryLinkListItemDto>();
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var idSet = workspaceRepositoryIds.ToHashSet();
        var rows = await Project(
                db.WorkspaceRepositories.AsNoTracking()
                    .Where(wr => wr.WorkspaceId == workspaceId && idSet.Contains(wr.WorkspaceRepositoryId)),
                db,
                contextId,
                isSpecialWorkspace)
            .ToListAsync(cancellationToken);

        var order = workspaceRepositoryIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        return rows
            .OrderBy(r => order.GetValueOrDefault(r.WorkspaceRepositoryId, int.MaxValue))
            .ToList();
    }

    public async Task<IReadOnlyList<int>> GetRepositoryIdsAtLevelAsync(
        int workspaceId,
        int? levelKey,
        string? search,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, search);
        var query = ApplyFilters(db.WorkspaceRepositories.AsNoTracking(), filter);

        if (isSpecialWorkspace || contextId is null)
        {
            return await query
                .Where(wr => wr.DependencyLevel == levelKey)
                .OrderBy(wr => wr.WorkspaceRepositoryId)
                .Select(wr => wr.RepositoryId)
                .ToListAsync(cancellationToken);
        }

        var cid = contextId.Value.Value;
        return await query
            .Where(wr => db.WorkspaceRepositoryContextStates
                .Where(s => s.WorkspaceFeatureContextId == cid && s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .Select(s => s.DependencyLevel)
                .FirstOrDefault() == levelKey)
            .OrderBy(wr => wr.WorkspaceRepositoryId)
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> GetAllSnapshotsAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId);
        query = ApplySort(query, db, contextId, isSpecialWorkspace);
        return await Project(query, db, contextId, isSpecialWorkspace).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetGitVersionNameMapAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (isSpecialWorkspace || contextId is null)
        {
            var rows = await db.WorkspaceRepositories.AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId
                    && wr.Repository != null
                    && wr.GitVersion != null
                    && wr.GitVersion != string.Empty)
                .Select(wr => new { wr.Repository!.RepositoryName, wr.GitVersion })
                .ToListAsync(cancellationToken);

            return ToNameVersionMap(rows.Select(r => (r.RepositoryName, r.GitVersion)));
        }

        var cid = contextId.Value.Value;
        var contextRows = await db.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && wr.Repository != null)
            .Select(wr => new
            {
                wr.Repository!.RepositoryName,
                GitVersion = db.WorkspaceRepositoryContextStates
                    .Where(s => s.WorkspaceFeatureContextId == cid && s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                    .Select(s => s.GitVersion)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return ToNameVersionMap(contextRows.Select(r => (r.RepositoryName, r.GitVersion)));
    }

    /// <summary>
    /// Builds the name-to-version lookup used for version-token tooltips. Repository names are not unique in
    /// this workspace's data (two different repos can share a display name), so a plain
    /// <c>ToDictionary</c> would throw on a duplicate key - keep the first match instead, same as before this
    /// was factored out.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ToNameVersionMap(IEnumerable<(string RepositoryName, string? GitVersion)> rows) =>
        rows
            .Where(r => !string.IsNullOrWhiteSpace(r.RepositoryName) && !string.IsNullOrEmpty(r.GitVersion))
            .GroupBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GitVersion!, StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspaceRepositoryLinkListItemDto?> GetSnapshotAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceFeatureContextId? contextId = null,
        bool isSpecialWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await Project(
                db.WorkspaceRepositories.AsNoTracking()
                    .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId),
                db,
                contextId,
                isSpecialWorkspace)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<WorkspaceRepositoryLink> ApplyFilters(
        IQueryable<WorkspaceRepositoryLink> query,
        WorkspaceRepositoryLinkListFilter filter)
    {
        query = query.Where(wr => wr.WorkspaceId == filter.WorkspaceId);
        return query.ApplySearch(filter.Search, WorkspaceRepositoryLinkSearchExpressions.BuildTermPredicate);
    }

    /// <summary>
    /// Sort/keyset/level-grouping must read the same source as <see cref="Project"/>: the shared link's
    /// <c>DependencyLevel</c>/<c>RepositoryType</c>/<c>Dependencies</c> for the special Workspace, or a
    /// correlated lookup into that context's <see cref="WorkspaceRepositoryContextState"/> row for a Feature
    /// (null when that context has no state row yet - never the Workspace's own value). See design doc §6.4.
    /// </summary>
    private static IQueryable<WorkspaceRepositoryLink> ApplySort(
        IQueryable<WorkspaceRepositoryLink> query,
        AppDbContext db,
        WorkspaceFeatureContextId? contextId,
        bool isSpecialWorkspace)
    {
        if (isSpecialWorkspace || contextId is null)
        {
            return query
                .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
                .ThenBy(wr => wr.RepositoryType == ProjectType.Service ? 0
                    : wr.RepositoryType == ProjectType.Package ? 1
                    : wr.RepositoryType == ProjectType.Executable ? 2
                    : wr.RepositoryType == ProjectType.Library ? 3
                    : wr.RepositoryType == ProjectType.Test ? 4
                    : 5)
                .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
                .ThenBy(wr => wr.WorkspaceRepositoryId);
        }

        var cid = contextId.Value.Value;
        var states = db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == cid);

        return query
            .OrderByDescending(wr => (states
                .Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .Select(s => s.DependencyLevel)
                .FirstOrDefault()) ?? int.MinValue)
            .ThenBy(wr => states
                .Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .Select(s => s.RepositoryType)
                .FirstOrDefault() == ProjectType.Service ? 0
                : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Package ? 1
                : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Executable ? 2
                : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Library ? 3
                : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Test ? 4
                : 5)
            .ThenByDescending(wr => (states
                .Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .Select(s => s.Dependencies)
                .FirstOrDefault()) ?? int.MinValue)
            .ThenBy(wr => wr.WorkspaceRepositoryId);
    }

    private static IQueryable<WorkspaceRepositoryLink> ApplyKeyset(
        IQueryable<WorkspaceRepositoryLink> query,
        WorkspaceRepositoryLinkListCursor? cursor,
        AppDbContext db,
        WorkspaceFeatureContextId? contextId,
        bool isSpecialWorkspace)
    {
        if (cursor is null)
        {
            return query;
        }

        if (isSpecialWorkspace || contextId is null)
        {
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

        var cid = contextId.Value.Value;
        var states = db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == cid);

        return query.Where(wr =>
            ((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.DependencyLevel).FirstOrDefault()) ?? int.MinValue) < cursor.DependencyLevelSortKey
            || (((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.DependencyLevel).FirstOrDefault()) ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Service ? 0
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Package ? 1
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Executable ? 2
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Library ? 3
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Test ? 4
                    : 5) > cursor.RepositoryTypeSortKey)
            || (((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.DependencyLevel).FirstOrDefault()) ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Service ? 0
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Package ? 1
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Executable ? 2
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Library ? 3
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Test ? 4
                    : 5) == cursor.RepositoryTypeSortKey
                && ((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.Dependencies).FirstOrDefault()) ?? int.MinValue) < cursor.DependenciesSortKey)
            || (((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.DependencyLevel).FirstOrDefault()) ?? int.MinValue) == cursor.DependencyLevelSortKey
                && (states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Service ? 0
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Package ? 1
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Executable ? 2
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Library ? 3
                    : states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.RepositoryType).FirstOrDefault() == ProjectType.Test ? 4
                    : 5) == cursor.RepositoryTypeSortKey
                && ((states.Where(s => s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId).Select(s => s.Dependencies).FirstOrDefault()) ?? int.MinValue) == cursor.DependenciesSortKey
                && wr.WorkspaceRepositoryId > cursor.WorkspaceRepositoryId));
    }

    /// <summary>
    /// Projects link rows to grid DTOs. For the special Workspace context (or when no context is supplied,
    /// preserving legacy callers), fields are read straight off <see cref="WorkspaceRepositoryLink"/> as before.
    /// For a Feature context, the checkout/PR/dependency fields are overlaid from
    /// <see cref="WorkspaceRepositoryContextState"/> / <see cref="WorkspaceRepositoryContextPullRequest"/> /
    /// <see cref="WorkspaceGitContextChangeEntry"/> for that context - see design doc §6.4/§6.5/§23.
    /// Repository-identity fields (name, clone URL, archived, default branch name) are shared across
    /// contexts and always come from the link/Repository.
    /// </summary>
    private static IQueryable<WorkspaceRepositoryLinkListItemDto> Project(
        IQueryable<WorkspaceRepositoryLink> query,
        AppDbContext db,
        WorkspaceFeatureContextId? contextId,
        bool isSpecialWorkspace)
    {
        if (isSpecialWorkspace || contextId is null)
        {
            return query.Select(wr => new WorkspaceRepositoryLinkListItemDto(
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
                wr.HasSelfFileVersionToken,
                wr.PullRequest != null ? wr.PullRequest.State : null,
                wr.PullRequest != null ? wr.PullRequest.PullRequestNumber : null,
                wr.PullRequest != null ? wr.PullRequest.HtmlUrl : null,
                wr.PullRequest != null ? wr.PullRequest.MergedAt : null,
                wr.PullRequest != null ? wr.PullRequest.Mergeable : null,
                wr.PullRequest != null ? wr.PullRequest.MergeableState : null,
                wr.PullRequest != null ? wr.PullRequest.ChangedFiles : null,
                wr.Repository != null && wr.Repository.Archived,
                wr.GitChangeEntries.Count()));
        }

        var cid = contextId.Value.Value;
        var joined =
            from wr in query
            join s in db.WorkspaceRepositoryContextStates.AsNoTracking().Where(s => s.WorkspaceFeatureContextId == cid)
                on wr.WorkspaceRepositoryId equals s.WorkspaceRepositoryId into states
            from state in states.DefaultIfEmpty()
            join pr in db.WorkspaceRepositoryContextPullRequests.AsNoTracking().Where(p => p.WorkspaceFeatureContextId == cid)
                on wr.WorkspaceRepositoryId equals pr.WorkspaceRepositoryId into prs
            from pr in prs.DefaultIfEmpty()
            select new { wr, state, pr };

        return joined.Select(x => new WorkspaceRepositoryLinkListItemDto(
            x.wr.WorkspaceRepositoryId,
            x.wr.WorkspaceId,
            x.wr.RepositoryId,
            x.wr.Repository != null ? x.wr.Repository.RepositoryName : string.Empty,
            x.wr.Repository != null ? x.wr.Repository.CloneUrl : string.Empty,
            x.state != null ? x.state.GitVersion : null,
            x.state != null ? x.state.BranchName : null,
            x.state != null ? x.state.CheckedOutTag : null,
            x.wr.DefaultBranchName,
            x.state != null ? x.state.OutgoingCommits : null,
            x.state != null ? x.state.IncomingCommits : null,
            x.state != null ? x.state.DefaultBranchBehindCommits : null,
            x.state != null ? x.state.DefaultBranchAheadCommits : null,
            x.state != null ? x.state.BranchHasUpstream : null,
            x.state != null ? x.state.SyncStatus : RepoSyncStatus.NeedsSync,
            x.state != null ? x.state.DependencyLevel : null,
            x.state != null ? x.state.Dependencies : null,
            x.state != null ? x.state.UnmatchedDeps : null,
            x.state != null ? x.state.OutOfDateFileRepos : null,
            x.state != null ? x.state.RepositoryType : null,
            x.state != null ? x.state.HasNewerTag : null,
            x.state != null ? x.state.HasSelfFileVersionToken : null,
            x.pr != null ? x.pr.State : null,
            x.pr != null ? x.pr.PullRequestNumber : null,
            x.pr != null ? x.pr.HtmlUrl : null,
            x.pr != null ? x.pr.MergedAt : null,
            x.pr != null ? x.pr.Mergeable : null,
            x.pr != null ? x.pr.MergeableState : null,
            x.pr != null ? x.pr.ChangedFiles : null,
            x.wr.Repository != null && x.wr.Repository.Archived,
            db.WorkspaceGitContextChangeEntries.Count(e => e.WorkspaceFeatureContextId == cid && e.WorkspaceRepositoryId == x.wr.WorkspaceRepositoryId)));
    }

    private static WorkspaceRepositoryLinkListCursor ToCursor(WorkspaceRepositoryLinkListItemDto dto) =>
        new(
            dto.DependencyLevel ?? int.MinValue,
            WorkspaceRepositoryLinkSearchExpressions.GetRepositoryTypeSortKey(dto.RepositoryType),
            dto.Dependencies ?? int.MinValue,
            dto.WorkspaceRepositoryId);
}

