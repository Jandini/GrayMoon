using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
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

        var repositories = statuses
            .Where(s => repoInfoById.ContainsKey(s.WorkspaceRepositoryId))
            .Select(s =>
            {
                var info = repoInfoById[s.WorkspaceRepositoryId];
                return new WorkspaceGitChangesRepositorySummary
                {
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
        var queryStarted = Stopwatch.GetTimestamp();
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

        GitChangesBench.Log(
            _logger,
            _gitChangesOptions.Value,
            "entry page",
            pathCount: page.Count,
            elapsedMs: GitChangesBench.ElapsedMs(queryStarted),
            ("PrefixDepth", prefix.Length == 0 ? 0 : prefix.Count(c => c == '/') + 1),
            ("Take", take),
            ("ReturnedCount", page.Count),
            ("TotalDirectChildCount", total),
            ("QueryMs", GitChangesBench.ElapsedMs(queryStarted)),
            ("WorkspaceId", workspaceId),
            ("ContextId", contextId.Value),
            ("WorkspaceRepositoryId", workspaceRepositoryId));

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
