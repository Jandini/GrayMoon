using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Read-only access to the persisted Git Changes projection. Never contacts the Worker - opening or
/// reloading the Git Changes page must read SQLite only, per the feature's architecture.
/// </summary>
public interface IWorkspaceGitChangesReadService
{
    Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken);

    Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);

    /// <summary>Layer A: repositories and counts with no entry lists, plus distinct path count.</summary>
    Task<WorkspaceGitChangesSummariesView> GetContextSummariesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);

    /// <summary>Full entry list for one repository in a context (small-mode live patch / expand hydrate).</summary>
    Task<WorkspaceGitChangesRepositoryView?> GetRepositoryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One repository's status and counts with no entry list (large-mode live patch). Cost is independent of
    /// the repository's path count apart from one grouped kind-count query.
    /// </summary>
    Task<WorkspaceGitChangesRepositoryView?> GetRepositorySummaryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every path (files and untracked directory entries) in one section at or below <paramref name="pathPrefix"/>
    /// (empty = whole repository), ordered ignore-case. Used by bulk consumers (copy all, undo confirmation)
    /// that must cover paths the circuit has not materialised. The list is transient, never retained.
    /// </summary>
    Task<IReadOnlyList<string>> GetEntryPathsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        CancellationToken cancellationToken);

    /// <summary>True when <paramref name="path"/> exists in the given section of one repository (one indexed lookup).</summary>
    Task<bool> EntryExistsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string path,
        CancellationToken cancellationToken);

    /// <summary>
    /// Direct children under <paramref name="pathPrefix"/> for one section, keyset-paged.
    /// <paramref name="afterChildKey"/> is the previous page's <see cref="GitChangesTreeChild.ChildKey"/> (or null for the first page).
    /// </summary>
    Task<GitChangesEntryPage> GetEntryPageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        string? afterChildKey,
        int take,
        string? filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cross-repository search over the persisted inventory, keyset-paged. Only matching entries are ever
    /// materialised; non-matching rows are streamed past and dropped. An empty filter matches every entry.
    /// Evaluates the same predicate as <see cref="Ui.WorkspaceGitChangeSearchMatcher"/>, so results equal the
    /// in-circuit filter used for small inventories. <paramref name="afterKey"/> is the previous page's
    /// <see cref="GitChangesSearchPage.NextAfterKey"/> (null for the first page, which also reports the total).
    /// </summary>
    Task<GitChangesSearchPage> SearchEntriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? filter,
        string? afterKey,
        int take,
        CancellationToken cancellationToken);

    /// <summary>True when any repository in the context has a staged, unstaged, or conflicted file.</summary>
    Task<bool> HasAnyChangesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceGitChangesReadService : IWorkspaceGitChangesReadService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOptions<GitChangesOptions> _gitChangesOptions;
    private readonly ILogger<WorkspaceGitChangesReadService> _logger;

    public WorkspaceGitChangesReadService(IDbContextFactory<AppDbContext> dbContextFactory)
        : this(dbContextFactory, Options.Create(new GitChangesOptions()), NullLogger<WorkspaceGitChangesReadService>.Instance)
    {
    }

    public WorkspaceGitChangesReadService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOptions<GitChangesOptions> gitChangesOptions,
        ILogger<WorkspaceGitChangesReadService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _gitChangesOptions = gitChangesOptions;
        _logger = logger;
    }

    public async Task<WorkspaceGitChangesView> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var contextId = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace)
            .Select(c => (int?)c.WorkspaceFeatureContextId)
            .FirstOrDefaultAsync(cancellationToken);
        if (contextId is null)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        return await LoadContextAsync(db, workspaceId, contextId.Value, cancellationToken);
    }

    public async Task<WorkspaceGitChangesView> GetContextAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadContextAsync(db, workspaceId, contextId.Value, cancellationToken);
    }

    public async Task<WorkspaceGitChangesSummariesView> GetContextSummariesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var statuses = await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId.Value && wr.WorkspaceId == workspaceId
            select s).ToListAsync(cancellationToken);

        if (statuses.Count == 0)
        {
            return new WorkspaceGitChangesSummariesView
            {
                WorkspaceId = workspaceId,
                TotalEntryCount = 0,
                Repositories = [],
            };
        }

        var workspaceRepositoryIds = statuses.Select(s => s.WorkspaceRepositoryId).ToList();

        var totalEntryCount = await db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .CountAsync(
                e => e.WorkspaceFeatureContextId == contextId.Value
                     && workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId),
                cancellationToken);

        var repoInfoById = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => workspaceRepositoryIds.Contains(wr.WorkspaceRepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, cancellationToken);

        var kindCountsByRepo = await LoadChangedKindCountsAsync(db, contextId.Value, workspaceRepositoryIds, cancellationToken);

        var repositories = statuses
            .Where(s => repoInfoById.ContainsKey(s.WorkspaceRepositoryId))
            .Select(s =>
            {
                var info = repoInfoById[s.WorkspaceRepositoryId];
                return new WorkspaceGitChangesRepositorySummary
                {
                    ChangedKindCounts = kindCountsByRepo.GetValueOrDefault(s.WorkspaceRepositoryId)
                                        ?? new Dictionary<GitChangeKind, int>(),
                    WorkspaceRepositoryId = s.WorkspaceRepositoryId,
                    RepositoryId = info.RepositoryId,
                    RepositoryName = info.RepositoryName,
                    BranchName = s.BranchName,
                    DefaultBranchName = info.DefaultBranchName,
                    HeadCommit = s.HeadCommit,
                    IsDetachedHead = s.IsDetachedHead,
                    IsUnbornBranch = s.IsUnbornBranch,
                    IsMerging = s.IsMerging,
                    IsRebasing = s.IsRebasing,
                    IsCherryPicking = s.IsCherryPicking,
                    StagedCount = s.StagedCount,
                    ChangedCount = s.ChangedCount,
                    ConflictCount = s.ConflictCount,
                    Insertions = s.Insertions,
                    Deletions = s.Deletions,
                    StagedInsertions = s.StagedInsertions,
                    StagedDeletions = s.StagedDeletions,
                    WorkerScannedAt = s.WorkerScannedAt,
                    PersistedAt = s.PersistedAt,
                    ContentDigest = s.ContentDigest,
                    LastErrorCode = s.LastErrorCode,
                    LastErrorMessage = s.LastErrorMessage,
                    SnapshotVersion = s.SnapshotVersion,
                };
            })
            .OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceGitChangesSummariesView
        {
            WorkspaceId = workspaceId,
            TotalEntryCount = totalEntryCount,
            Repositories = repositories,
        };
    }

    public async Task<WorkspaceGitChangesRepositoryView?> GetRepositoryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId)
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
            return null;

        var status = await db.WorkspaceGitContextRepositoryStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value
                     && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId,
                cancellationToken);
        if (status is null)
            return null;

        var entries = await db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value
                        && e.WorkspaceRepositoryId == link.WorkspaceRepositoryId)
            .ToListAsync(cancellationToken);

        return MapRepository(status, link.RepositoryId, link.RepositoryName, link.DefaultBranchName, entries);
    }

    public async Task<WorkspaceGitChangesRepositoryView?> GetRepositorySummaryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId)
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
            return null;

        var status = await db.WorkspaceGitContextRepositoryStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value
                     && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId,
                cancellationToken);
        if (status is null)
            return null;

        var kindCounts = await LoadChangedKindCountsAsync(
            db, contextId.Value, [link.WorkspaceRepositoryId], cancellationToken);

        return MapRepository(status, link.RepositoryId, link.RepositoryName, link.DefaultBranchName, [])
            with
            {
                ChangedKindCounts = kindCounts.GetValueOrDefault(link.WorkspaceRepositoryId)
                                    ?? new Dictionary<GitChangeKind, int>(),
            };
    }

    public async Task<IReadOnlyList<string>> GetEntryPathsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var belongs = await db.WorkspaceRepositories
            .AsNoTracking()
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
        if (!belongs)
            return [];

        var prefix = NormalizePrefix(pathPrefix);
        var query = db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value
                        && e.WorkspaceRepositoryId == workspaceRepositoryId);

        query = isStagedSection
            ? query.Where(e => e.IndexChange != GitChangeKind.None)
            : query.Where(e => e.WorktreeChange != GitChangeKind.None);

        if (prefix.Length > 0)
        {
            var rangeStart = prefix + "/";
            var rangeEnd = prefix + "0";
            query = query.Where(e => e.Path.CompareTo(rangeStart) >= 0 && e.Path.CompareTo(rangeEnd) < 0);
        }

        var paths = await query.Select(e => e.Path).ToListAsync(cancellationToken);
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    public async Task<bool> EntryExistsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string path,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var belongs = await db.WorkspaceRepositories
            .AsNoTracking()
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
        if (!belongs)
            return false;

        var query = db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value
                        && e.WorkspaceRepositoryId == workspaceRepositoryId
                        && e.Path == path);

        return isStagedSection
            ? await query.AnyAsync(e => e.IndexChange != GitChangeKind.None, cancellationToken)
            : await query.AnyAsync(e => e.WorktreeChange != GitChangeKind.None, cancellationToken);
    }

    private static string NormalizePrefix(string? pathPrefix) =>
        pathPrefix?.Replace('\\', '/').Trim('/') ?? string.Empty;

    private static async Task<Dictionary<int, Dictionary<GitChangeKind, int>>> LoadChangedKindCountsAsync(
        AppDbContext db,
        int contextId,
        IReadOnlyCollection<int> workspaceRepositoryIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId
                        && workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId)
                        && e.WorktreeChange != GitChangeKind.None)
            .GroupBy(e => new { e.WorkspaceRepositoryId, e.WorktreeChange, e.IsConflicted })
            .Select(g => new { g.Key.WorkspaceRepositoryId, g.Key.WorktreeChange, g.Key.IsConflicted, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, Dictionary<GitChangeKind, int>>();
        foreach (var row in rows)
        {
            // Same classification as GitChangesSummaryStats.Classify.
            var kind = row.IsConflicted
                ? GitChangeKind.Unmerged
                : row.WorktreeChange == GitChangeKind.Untracked ? GitChangeKind.Added : row.WorktreeChange;

            if (!result.TryGetValue(row.WorkspaceRepositoryId, out var perRepo))
                result[row.WorkspaceRepositoryId] = perRepo = new Dictionary<GitChangeKind, int>();
            perRepo[kind] = perRepo.GetValueOrDefault(kind) + row.Count;
        }

        return result;
    }

    private sealed class ChildRow
    {
        public int Grp { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Kind { get; set; }
        public int? EntryId { get; set; }
    }

    public async Task<GitChangesEntryPage> GetEntryPageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        string? afterChildKey,
        int take,
        string? filter,
        CancellationToken cancellationToken)
    {
        // A text filter needs the in-process matcher (Phase 2 moves this to SQL); the unfiltered tree path
        // groups and keyset-pages entirely in SQLite so only one page of rows is ever materialized.
        if (!string.IsNullOrWhiteSpace(filter))
        {
            return await GetEntryPageInMemoryAsync(
                workspaceId, contextId, workspaceRepositoryId, isStagedSection, pathPrefix, afterChildKey, take, filter,
                cancellationToken);
        }

        // Callers may ask for any page size; clamp so one request stays bounded (TreePageSize x 4 at most).
        take = Math.Clamp(take, 1, Math.Max(1, _gitChangesOptions.Value.TreePageSize * 4));

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var belongs = await db.WorkspaceRepositories
            .AsNoTracking()
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
        if (!belongs)
        {
            return new GitChangesEntryPage
            {
                SnapshotVersion = 0,
                TotalDirectChildCount = 0,
                Children = [],
                NextAfterChildKey = null,
            };
        }

        var snapshotVersion = await db.WorkspaceGitContextRepositoryStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value
                        && s.WorkspaceRepositoryId == workspaceRepositoryId)
            .Select(s => (long?)s.SnapshotVersion)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

        var prefix = NormalizePrefix(pathPrefix);
        var parameters = new List<SqliteParameter>
        {
            new("@ctx", contextId.Value),
            new("@repo", workspaceRepositoryId),
            new("@start", prefix.Length == 0 ? 1 : prefix.Length + 2),
        };

        var sectionSql = isStagedSection
            ? "e.\"IndexChange\" <> 0"
            : "e.\"WorktreeChange\" <> 0";

        var rangeSql = string.Empty;
        if (prefix.Length > 0)
        {
            // Binary range on the (ctx, repo, Path) index; '0' is the character after '/'.
            rangeSql = " AND e.\"Path\" >= @lo AND e.\"Path\" < @hi AND e.\"Path\" <> @exact";
            parameters.Add(new SqliteParameter("@lo", prefix + "/"));
            parameters.Add(new SqliteParameter("@hi", prefix + "0"));
            parameters.Add(new SqliteParameter("@exact", prefix + "/"));
        }

        // Group by next path segment. Kind: 0 = folder (deeper paths exist), 1 = untracked directory entry
        // ("name/"), 2 = file. Folder-ish and file groups are separate (Grp 0 / 1), a folder wins over a
        // directory entry of the same name, matching GroupDirectChildren.
        var cte = $"""
            WITH scoped AS (
                SELECT e."WorkspaceGitContextChangeEntryId" AS Id,
                       CASE WHEN substr(e."Path", -1) = '/'
                            THEN substr(e."Path", @start, length(e."Path") - @start)
                            ELSE substr(e."Path", @start) END AS Rem,
                       CASE WHEN substr(e."Path", -1) = '/' THEN 1 ELSE 0 END AS IsDir
                FROM "WorkspaceGitContextChangeEntries" e
                WHERE e."WorkspaceFeatureContextId" = @ctx
                  AND e."WorkspaceRepositoryId" = @repo
                  AND {sectionSql}{rangeSql}
            ),
            kinds AS (
                SELECT Id,
                       CASE WHEN instr(Rem, '/') > 0 THEN substr(Rem, 1, instr(Rem, '/') - 1) ELSE Rem END AS Name,
                       CASE WHEN instr(Rem, '/') > 0 THEN 0 WHEN IsDir = 1 THEN 1 ELSE 2 END AS Kind
                FROM scoped
                WHERE length(Rem) > 0
            ),
            grouped AS (
                SELECT CASE WHEN Kind = 2 THEN 1 ELSE 0 END AS Grp,
                       Name,
                       MIN(Kind) AS Kind,
                       MIN(CASE WHEN Kind > 0 THEN Id END) AS EntryId
                FROM kinds
                GROUP BY Grp, Name
            )
            """;

        // The concatenated fragments (cte, keysetSql) are compile-time constants or built from constants only;
        // every user-derived value (paths, keys, ids) is a SqliteParameter.
#pragma warning disable EF1003
        var total = await db.Database
            .SqlQueryRaw<int>(cte + " SELECT COUNT(*) AS \"Value\" FROM grouped", CloneParameters(parameters))
            .SingleAsync(cancellationToken);

        var keysetSql = string.Empty;
        var pageParameters = CloneParameters(parameters).ToList();
        if (!string.IsNullOrEmpty(afterChildKey) && afterChildKey.Length >= 2 && afterChildKey[1] == ':')
        {
            var afterGrp = afterChildKey[0] == 'p' ? 1 : 0;
            var afterName = afterChildKey[2..];
            keysetSql = """
                 WHERE (Grp > @ag
                        OR (Grp = @ag AND (Name COLLATE NOCASE > @an COLLATE NOCASE
                                           OR (Name COLLATE NOCASE = @an COLLATE NOCASE AND Name > @an))))
                """;
            pageParameters.Add(new SqliteParameter("@ag", afterGrp));
            pageParameters.Add(new SqliteParameter("@an", afterName));
        }

        pageParameters.Add(new SqliteParameter("@limit", take + 1));
        var rows = await db.Database
            .SqlQueryRaw<ChildRow>(
                cte + " SELECT Grp, Name, Kind, EntryId FROM grouped" + keysetSql
                    + " ORDER BY Grp, Name COLLATE NOCASE, Name LIMIT @limit",
                pageParameters.Cast<object>().ToArray())
            .ToListAsync(cancellationToken);
#pragma warning restore EF1003

        var hasMore = rows.Count > take;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var entryIds = rows.Where(r => r.EntryId.HasValue && (r.Grp == 1 || r.Kind == 1))
            .Select(r => r.EntryId!.Value)
            .ToList();
        var entriesById = entryIds.Count == 0
            ? new Dictionary<int, WorkspaceGitContextChangeEntry>()
            : await db.WorkspaceGitContextChangeEntries
                .AsNoTracking()
                .Where(e => entryIds.Contains(e.WorkspaceGitContextChangeEntryId))
                .ToDictionaryAsync(e => e.WorkspaceGitContextChangeEntryId, cancellationToken);

        var children = new List<GitChangesTreeChild>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Grp == 0 && row.Kind == 0)
            {
                children.Add(new GitChangesTreeChild
                {
                    IsFolder = true,
                    Name = row.Name,
                    Path = prefix.Length == 0 ? row.Name : $"{prefix}/{row.Name}",
                    Entry = null,
                });
                continue;
            }

            if (row.EntryId is not int id || !entriesById.TryGetValue(id, out var entity))
                continue;

            var entry = ToEntryView(entity);
            children.Add(row.Grp == 0
                ? new GitChangesTreeChild
                {
                    IsFolder = true,
                    Name = row.Name,
                    Path = entry.Path,
                    Entry = entry,
                    IsDirectoryEntry = true,
                }
                : new GitChangesTreeChild
                {
                    IsFolder = false,
                    Name = row.Name,
                    Path = entry.Path,
                    Entry = entry,
                });
        }

        var result = new GitChangesEntryPage
        {
            SnapshotVersion = snapshotVersion,
            TotalDirectChildCount = total,
            Children = children,
            NextAfterChildKey = hasMore && children.Count > 0 ? children[^1].ChildKey : null,
        };

        return result;
    }

    // SqliteParameter instances cannot be shared between commands.
    private static object[] CloneParameters(IEnumerable<SqliteParameter> source) =>
        source.Select(p => (object)new SqliteParameter(p.ParameterName, p.Value)).ToArray();

    private async Task<GitChangesEntryPage> GetEntryPageInMemoryAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        string? afterChildKey,
        int take,
        string? filter,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, Math.Max(1, _gitChangesOptions.Value.TreePageSize * 4));

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var belongs = await (
            from wr in db.WorkspaceRepositories.AsNoTracking()
            where wr.WorkspaceId == workspaceId && wr.WorkspaceRepositoryId == workspaceRepositoryId
            select wr.WorkspaceRepositoryId).AnyAsync(cancellationToken);
        if (!belongs)
        {
            return new GitChangesEntryPage
            {
                SnapshotVersion = 0,
                TotalDirectChildCount = 0,
                Children = [],
                NextAfterChildKey = null,
            };
        }

        var snapshotVersion = await db.WorkspaceGitContextRepositoryStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value
                        && s.WorkspaceRepositoryId == workspaceRepositoryId)
            .Select(s => (long?)s.SnapshotVersion)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

        var prefix = pathPrefix?.Replace('\\', '/').Trim('/') ?? string.Empty;
        var entriesQuery = db.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId.Value
                        && e.WorkspaceRepositoryId == workspaceRepositoryId);

        entriesQuery = isStagedSection
            ? entriesQuery.Where(e => e.IndexChange != GitChangeKind.None)
            : entriesQuery.Where(e => e.WorktreeChange != GitChangeKind.None);

        if (prefix.Length > 0)
        {
            var rangeStart = prefix + "/";
            var rangeEnd = prefix + "0";
            entriesQuery = entriesQuery.Where(e =>
                e.Path == prefix + "/"
                || (e.Path.CompareTo(rangeStart) >= 0 && e.Path.CompareTo(rangeEnd) < 0));
        }

        var entries = await entriesQuery.ToListAsync(cancellationToken);
        var repoName = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceRepositoryId == workspaceRepositoryId)
            .Select(wr => wr.Repository!.RepositoryName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        IEnumerable<WorkspaceGitContextChangeEntry> filtered = entries;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filtered = entries.Where(e => WorkspaceGitChangeSearchMatcher.Matches(
                repoName,
                ToEntryView(e),
                filter));
        }

        var children = GroupDirectChildren(prefix, filtered.Select(ToEntryView));
        var ordered = OrderChildren(children).ToList();
        var total = ordered.Count;

        if (!string.IsNullOrEmpty(afterChildKey))
        {
            var idx = ordered.FindIndex(c => c.ChildKey == afterChildKey);
            ordered = idx >= 0 ? ordered.Skip(idx + 1).ToList() : ordered;
        }

        var page = ordered.Take(take).ToList();
        var nextKey = page.Count > 0 && page.Count < ordered.Count
            ? page[^1].ChildKey
            : null;

        var result = new GitChangesEntryPage
        {
            SnapshotVersion = snapshotVersion,
            TotalDirectChildCount = total,
            Children = page,
            NextAfterChildKey = nextKey,
        };

        return result;
    }

    public async Task<GitChangesSearchPage> SearchEntriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? filter,
        string? afterKey,
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, Math.Max(1, _gitChangesOptions.Value.TreePageSize * 4));

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var repos = await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId.Value && wr.WorkspaceId == workspaceId
            orderby wr.WorkspaceRepositoryId
            select new
            {
                wr.WorkspaceRepositoryId,
                RepositoryName = wr.Repository!.RepositoryName,
                s.SnapshotVersion,
            }).ToListAsync(cancellationToken);

        // Only used to detect "a snapshot landed between pages". Per-repository versions can be huge (they are
        // not small counters), so fold them with unchecked arithmetic instead of a checked Sum, which overflows.
        var snapshotVersion = repos.Aggregate(17L, (acc, r) => unchecked((acc * 31) + r.SnapshotVersion));
        var matches = WorkspaceGitChangeSearchMatcher.Compile(filter);

        var firstPage = string.IsNullOrEmpty(afterKey);
        var cursorRepoId = int.MinValue;
        string? cursorPath = null;
        if (!firstPage)
        {
            var sep = afterKey!.IndexOf('|');
            if (sep > 0 && int.TryParse(afterKey.AsSpan(0, sep), out var parsedRepoId))
            {
                cursorRepoId = parsedRepoId;
                cursorPath = afterKey[(sep + 1)..];
            }
            else
            {
                firstPage = true; // Unreadable cursor: restart rather than return nothing.
            }
        }

        var hits = new List<GitChangesSearchHit>(take);
        var totalMatches = 0;
        var hasMore = false;
        string? nextKey = null;

        foreach (var repo in repos)
        {
            if (repo.WorkspaceRepositoryId < cursorRepoId)
                continue;

            var query = db.WorkspaceGitContextChangeEntries
                .AsNoTracking()
                .Where(e => e.WorkspaceFeatureContextId == contextId.Value
                            && e.WorkspaceRepositoryId == repo.WorkspaceRepositoryId
                            && (e.IndexChange != GitChangeKind.None || e.WorktreeChange != GitChangeKind.None));

            if (cursorPath is not null && repo.WorkspaceRepositoryId == cursorRepoId)
            {
                var after = cursorPath;
                query = query.Where(e => e.Path.CompareTo(after) > 0);
            }

            // Streamed in path order on the (ctx, repo, Path) index: rows that do not match are dropped as soon
            // as they are read, so memory is bounded by the page, not by the repository's inventory.
            await foreach (var entity in query.OrderBy(e => e.Path).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var view = ToEntryView(entity);
                if (!matches(repo.RepositoryName, view))
                    continue;

                if (hits.Count < take)
                {
                    hits.Add(new GitChangesSearchHit
                    {
                        WorkspaceRepositoryId = repo.WorkspaceRepositoryId,
                        RepositoryName = repo.RepositoryName,
                        Entry = view,
                    });
                    totalMatches++;
                    continue;
                }

                hasMore = true;
                if (!firstPage)
                    break;

                // The first page also reports the total, so it keeps counting past the page boundary.
                totalMatches++;
            }

            if (hasMore && !firstPage)
                break;
        }

        if (hasMore && hits.Count > 0)
        {
            var last = hits[^1];
            nextKey = $"{last.WorkspaceRepositoryId}|{last.Entry.Path}";
        }

        var result = new GitChangesSearchPage
        {
            SnapshotVersion = snapshotVersion,
            TotalMatchCount = firstPage ? totalMatches : null,
            Hits = hits,
            NextAfterKey = nextKey,
        };

        return result;
    }

    public async Task<bool> HasAnyChangesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId.Value
                  && wr.WorkspaceId == workspaceId
                  && (s.StagedCount > 0 || s.ChangedCount > 0 || s.ConflictCount > 0)
            select s).AnyAsync(cancellationToken);
    }

    private static async Task<WorkspaceGitChangesView> LoadContextAsync(
        AppDbContext db,
        int workspaceId,
        int contextId,
        CancellationToken cancellationToken)
    {
        var statuses = await (
            from s in db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
            join wr in db.WorkspaceRepositories.AsNoTracking()
                on s.WorkspaceRepositoryId equals wr.WorkspaceRepositoryId
            where s.WorkspaceFeatureContextId == contextId && wr.WorkspaceId == workspaceId
            select s).ToListAsync(cancellationToken);

        if (statuses.Count == 0)
            return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = [] };

        var workspaceRepositoryIds = statuses.Select(s => s.WorkspaceRepositoryId).ToList();

        var repoInfoById = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => workspaceRepositoryIds.Contains(wr.WorkspaceRepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, RepositoryName = wr.Repository!.RepositoryName, wr.DefaultBranchName })
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, cancellationToken);

        var entriesByRepo = (await db.WorkspaceGitContextChangeEntries
                .AsNoTracking()
                .Where(e => e.WorkspaceFeatureContextId == contextId
                            && workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId))
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var repositories = statuses
            .Where(s => repoInfoById.ContainsKey(s.WorkspaceRepositoryId))
            .Select(s =>
            {
                var info = repoInfoById[s.WorkspaceRepositoryId];
                var list = entriesByRepo.TryGetValue(s.WorkspaceRepositoryId, out var entries)
                    ? entries
                    : [];
                return MapRepository(s, info.RepositoryId, info.RepositoryName, info.DefaultBranchName, list);
            })
            .OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceGitChangesView { WorkspaceId = workspaceId, Repositories = repositories };
    }

    private static WorkspaceGitChangesRepositoryView MapRepository(
        WorkspaceGitContextRepositoryStatus s,
        int repositoryId,
        string repositoryName,
        string? defaultBranchName,
        IReadOnlyList<WorkspaceGitContextChangeEntry> entries) =>
        new()
        {
            WorkspaceRepositoryId = s.WorkspaceRepositoryId,
            RepositoryId = repositoryId,
            RepositoryName = repositoryName,
            BranchName = s.BranchName,
            DefaultBranchName = defaultBranchName,
            HeadCommit = s.HeadCommit,
            IsDetachedHead = s.IsDetachedHead,
            IsUnbornBranch = s.IsUnbornBranch,
            IsMerging = s.IsMerging,
            IsRebasing = s.IsRebasing,
            IsCherryPicking = s.IsCherryPicking,
            StagedCount = s.StagedCount,
            ChangedCount = s.ChangedCount,
            ConflictCount = s.ConflictCount,
            Insertions = s.Insertions,
            Deletions = s.Deletions,
            StagedInsertions = s.StagedInsertions,
            StagedDeletions = s.StagedDeletions,
            WorkerScannedAt = s.WorkerScannedAt,
            PersistedAt = s.PersistedAt,
            ContentDigest = s.ContentDigest,
            LastErrorCode = s.LastErrorCode,
            LastErrorMessage = s.LastErrorMessage,
            Changes = entries.Select(ToEntryView).ToList(),
        };

    private static WorkspaceGitChangeEntryView ToEntryView(WorkspaceGitContextChangeEntry e) => new()
    {
        Path = e.Path,
        OriginalPath = e.OriginalPath,
        IndexChange = e.IndexChange,
        WorktreeChange = e.WorktreeChange,
        IsTracked = e.IsTracked,
        IsConflicted = e.IsConflicted,
        IsSubmodule = e.IsSubmodule,
    };

    /// <summary>
    /// Groups path inventory into direct children of <paramref name="prefix"/> (empty = repo root).
    /// Matches <see cref="GitChangesTreeBuilder"/> folder/file/directory-entry rules.
    /// </summary>
    internal static List<GitChangesTreeChild> GroupDirectChildren(
        string prefix,
        IEnumerable<WorkspaceGitChangeEntryView> entries)
    {
        var prefixLen = prefix.Length;
        var folderNames = new HashSet<string>(StringComparer.Ordinal);
        var directoryEntries = new Dictionary<string, WorkspaceGitChangeEntryView>(StringComparer.Ordinal);
        var files = new Dictionary<string, WorkspaceGitChangeEntryView>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var path = entry.Path.Replace('\\', '/');
            string remainder;
            if (prefixLen == 0)
            {
                remainder = path.TrimStart('/');
            }
            else
            {
                if (path.Equals(prefix + "/", StringComparison.Ordinal))
                {
                    // Exact directory entry for the prefix itself is not a child of the prefix.
                    continue;
                }

                if (!path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    continue;

                remainder = path[(prefixLen + 1)..];
            }

            if (remainder.Length == 0)
                continue;

            // Directory entries ("Child/") are a single segment with a trailing slash on the full path.
            // Trim before deciding folder vs file so "nested/" is not treated as folder name "nested" plus empty.
            if (GitChangesTreeBuilder.IsDirectoryPath(path))
            {
                var dirRemainder = remainder.TrimEnd('/');
                var dirSlash = dirRemainder.IndexOf('/');
                if (dirSlash < 0)
                {
                    directoryEntries.TryAdd(dirRemainder, entry);
                    continue;
                }

                folderNames.Add(dirRemainder[..dirSlash]);
                continue;
            }

            var slash = remainder.IndexOf('/');
            if (slash < 0)
                files[remainder] = entry;
            else
                folderNames.Add(remainder[..slash]);
        }

        var children = new List<GitChangesTreeChild>();
        foreach (var name in folderNames)
        {
            var folderPath = prefixLen == 0 ? name : $"{prefix}/{name}";
            children.Add(new GitChangesTreeChild
            {
                IsFolder = true,
                Name = name,
                Path = folderPath,
                Entry = null,
            });
        }

        foreach (var (name, entry) in directoryEntries)
        {
            if (folderNames.Contains(name))
                continue;

            children.Add(new GitChangesTreeChild
            {
                IsFolder = true,
                Name = name,
                Path = entry.Path,
                Entry = entry,
                IsDirectoryEntry = true,
            });
        }

        foreach (var (name, entry) in files)
        {
            children.Add(new GitChangesTreeChild
            {
                IsFolder = false,
                Name = name,
                Path = entry.Path,
                Entry = entry,
            });
        }

        return children;
    }

    internal static IEnumerable<GitChangesTreeChild> OrderChildren(IEnumerable<GitChangesTreeChild> children) =>
        children
            .Where(c => c.IsFolder || c.IsDirectoryEntry)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(
                children
                    .Where(c => !c.IsFolder && !c.IsDirectoryEntry)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
}
