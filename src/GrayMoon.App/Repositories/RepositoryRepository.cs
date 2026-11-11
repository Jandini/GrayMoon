using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class GitHubRepositoryRepository(
    AppDbContext dbContext,
    ILogger<GitHubRepositoryRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<GitHubRepositoryRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<List<GitHubRepositoryEntry>> GetAllEntriesAsync()
    {
        return await _dbContext.Repositories
            .AsNoTracking()
            .Include(repository => repository.Connector)
            .OrderBy(repository => repository.RepositoryName)
            .Select(repository => new GitHubRepositoryEntry
            {
                RepositoryId = repository.RepositoryId,
                ConnectorName = repository.Connector == null ? "Unknown" : repository.Connector.RemovedAtUtc != null ? "No connector" : repository.Connector.ConnectorName,
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                Visibility = repository.Visibility,
                Archived = repository.Archived,
                CloneUrl = repository.CloneUrl,
                Topics = repository.Topics
            })
            .ToListAsync();
    }

    /// <summary>Connectors that have at least one stored repository.</summary>
    public async Task<HashSet<int>> GetConnectorIdsWithRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _dbContext.Repositories.AsNoTracking().Select(r => r.ConnectorId).Distinct().ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    /// <summary>Stored fork parent clone URLs keyed by (connector, provider repository id), so a fetch only asks the provider about forks whose parent is not known yet.</summary>
    public async Task<IReadOnlyDictionary<(int ConnectorId, long ProviderRepositoryId), string>> GetKnownForkParentsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.Repositories
            .AsNoTracking()
            .Where(r => r.IsFork && r.ParentCloneUrl != null && r.GitHubRepositoryId > 0)
            .Select(r => new { r.ConnectorId, r.GitHubRepositoryId, r.ParentCloneUrl })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => (r.ConnectorId, r.GitHubRepositoryId))
            .ToDictionary(g => g.Key, g => g.First().ParentCloneUrl!);
    }

    public async Task<Repository?> GetByIdAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Repositories
            .AsNoTracking()
            .Include(repository => repository.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);
    }

    public async Task<Repository?> GetByCloneUrlAsync(string cloneUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return null;
        return await _dbContext.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CloneUrl == cloneUrl.Trim(), cancellationToken);
    }

    public async Task<List<GitHubRepositoryEntry>> GetEntriesByConnectorIdAsync(int connectorId)
    {
        return await _dbContext.Repositories
            .AsNoTracking()
            .Include(repository => repository.Connector)
            .Where(repository => repository.ConnectorId == connectorId)
            .OrderBy(repository => repository.RepositoryName)
            .Select(repository => new GitHubRepositoryEntry
            {
                RepositoryId = repository.RepositoryId,
                ConnectorName = repository.Connector == null ? "Unknown" : repository.Connector.RemovedAtUtc != null ? "No connector" : repository.Connector.ConnectorName,
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                Visibility = repository.Visibility,
                Archived = repository.Archived,
                CloneUrl = repository.CloneUrl,
                Topics = repository.Topics
            })
            .ToListAsync();
    }

    /// <summary>Result of <see cref="GitHubRepositoryRepository.MergeRepositoriesAsync"/>.</summary>
    public sealed class MergeRepositoriesResult
    {
        public IReadOnlyList<RenamedRepositoryInfo> Renames { get; init; } = [];
        /// <summary>Maps a deleted duplicate RepositoryId to the surviving canonical RepositoryId. Empty when no merges were needed.</summary>
        public IReadOnlyDictionary<int, int> MergedRepositoryIdMap { get; init; } = new Dictionary<int, int>();
        /// <summary>Repositories no longer returned by the provider but kept because a Workspace links them. Empty when nothing was kept.</summary>
        public IReadOnlyList<KeptRepositoryInfo> KeptMissingFromProvider { get; init; } = [];
    }

    /// <summary>
    /// Merges fetched repositories with existing ones.
    /// Matching priority: (1) stable provider ID (ConnectorId + GitHubRepositoryId), (2) CloneUrl, (3) URL-prefix rename heuristic.
    /// Updates existing rows in-place - preserving RepositoryId and all workspace links.
    /// Adds new rows for genuinely new repositories. Handles multiple simultaneous renames in a single pass.
    /// <para>
    /// A row no longer returned is deleted only when its connector returned at least one repository in
    /// <paramref name="repositories"/> and no Workspace links it. A connector with nothing in the set (failed,
    /// inactive, or an empty answer) keeps every stored row, and a Workspace-linked repository is kept, marked
    /// with <see cref="Repository.MissingFromProviderSinceUtc"/> and reported in the result.
    /// </para>
    /// </summary>
    public async Task<MergeRepositoriesResult> MergeRepositoriesAsync(IReadOnlyCollection<Repository> repositories)
    {
        // Clear any stale Repository tracker state from previous operations in this circuit-scope DbContext.
        foreach (var entry in _dbContext.ChangeTracker.Entries<Repository>().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        // Normalize: require a non-empty CloneUrl; prefer the row with the highest GitHubRepositoryId if duplicate URLs.
        var normalized = repositories
            .Where(r => !string.IsNullOrWhiteSpace(r.CloneUrl))
            .Select(r =>
            {
                r.CloneUrl = r.CloneUrl.Trim();
                return r;
            })
            .GroupBy(r => r.CloneUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.GitHubRepositoryId).First())
            .ToList();

        _logger.LogDebug(
            "MergeRepositories: Starting. FetchedCount={FetchedCount}",
            normalized.Count);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        // Load ALL existing repository rows with AsNoTracking - never put them in the tracker here.
        var existing = await _dbContext.Repositories.AsNoTracking().ToListAsync();

        // Track which existing rows have been matched and which incoming repos have been matched.
        var matchedExistingIds = new HashSet<int>();
        var matchedIncomingIndexes = new HashSet<int>();
        var renames = new List<RenamedRepositoryInfo>();
        var updateCount = 0;
        var insertCount = 0;

        // ---------------------------------------------------------------------------
        // PHASE A - Match by stable provider ID: (ConnectorId, GitHubRepositoryId)
        // ---------------------------------------------------------------------------
        var existingByProviderId = existing
            .Where(r => r.GitHubRepositoryId > 0)
            .GroupBy(r => (r.ConnectorId, r.GitHubRepositoryId))
            .ToDictionary(g => g.Key, g => g.First());

        for (var i = 0; i < normalized.Count; i++)
        {
            var incoming = normalized[i];
            if (incoming.GitHubRepositoryId <= 0) continue;

            var key = (incoming.ConnectorId, incoming.GitHubRepositoryId);
            if (!existingByProviderId.TryGetValue(key, out var match)) continue;

            matchedExistingIds.Add(match.RepositoryId);
            matchedIncomingIndexes.Add(i);

            var nameChanged = !string.Equals(match.RepositoryName, incoming.RepositoryName, StringComparison.Ordinal);
            var urlChanged = !string.Equals(match.CloneUrl.Trim(), incoming.CloneUrl, StringComparison.OrdinalIgnoreCase);

            if (nameChanged || urlChanged
                || !string.Equals(match.OrgName, incoming.OrgName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(match.Visibility, incoming.Visibility, StringComparison.OrdinalIgnoreCase)
                || match.Archived != incoming.Archived
                || !string.Equals(match.Topics ?? string.Empty, incoming.Topics ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(match.NodeId, incoming.NodeId, StringComparison.Ordinal)
                || match.IsFork != incoming.IsFork
                || !string.Equals(match.ParentCloneUrl, ResolveParentCloneUrl(match, incoming), StringComparison.Ordinal))
            {
                if (nameChanged || urlChanged)
                {
                    renames.Add(new RenamedRepositoryInfo
                    {
                        OldName = match.RepositoryName,
                        NewName = incoming.RepositoryName,
                        OrgName = match.OrgName
                    });
                    _logger.LogInformation(
                        "MergeRepositories: Rename detected via provider ID. RepositoryId={RepositoryId}, OldName={OldName}, NewName={NewName}, OldCloneUrl={OldCloneUrl}, NewCloneUrl={NewCloneUrl}, ConnectorId={ConnectorId}, OrgName={OrgName}",
                        match.RepositoryId, match.RepositoryName, incoming.RepositoryName, match.CloneUrl, incoming.CloneUrl, match.ConnectorId, match.OrgName);
                }

                var parentCloneUrl = ResolveParentCloneUrl(match, incoming);
                await _dbContext.Repositories
                    .Where(r => r.RepositoryId == match.RepositoryId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.RepositoryName, incoming.RepositoryName)
                        .SetProperty(r => r.CloneUrl, incoming.CloneUrl)
                        .SetProperty(r => r.OrgName, incoming.OrgName)
                        .SetProperty(r => r.Visibility, incoming.Visibility)
                    .SetProperty(r => r.Archived, incoming.Archived)
                        .SetProperty(r => r.Topics, incoming.Topics)
                        .SetProperty(r => r.NodeId, incoming.NodeId)
                        .SetProperty(r => r.IsFork, incoming.IsFork)
                        .SetProperty(r => r.ParentCloneUrl, parentCloneUrl));
                updateCount++;
            }
        }

        // ---------------------------------------------------------------------------
        // PHASE B - Match unmatched by CloneUrl (handles legacy rows with GitHubRepositoryId = 0)
        // ---------------------------------------------------------------------------
        var existingByUrl = existing
            .Where(r => !matchedExistingIds.Contains(r.RepositoryId))
            .GroupBy(r => r.CloneUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < normalized.Count; i++)
        {
            if (matchedIncomingIndexes.Contains(i)) continue;
            var incoming = normalized[i];

            if (!existingByUrl.TryGetValue(incoming.CloneUrl, out var match)) continue;

            matchedExistingIds.Add(match.RepositoryId);
            matchedIncomingIndexes.Add(i);

            // Update in-place (always update GitHubRepositoryId if we now have it)
            var parentCloneUrlB = ResolveParentCloneUrl(match, incoming);
            await _dbContext.Repositories
                .Where(r => r.RepositoryId == match.RepositoryId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ConnectorId, incoming.ConnectorId)
                    .SetProperty(r => r.RepositoryName, incoming.RepositoryName)
                    .SetProperty(r => r.OrgName, incoming.OrgName)
                    .SetProperty(r => r.Visibility, incoming.Visibility)
                    .SetProperty(r => r.Archived, incoming.Archived)
                    .SetProperty(r => r.Topics, incoming.Topics)
                    .SetProperty(r => r.GitHubRepositoryId, incoming.GitHubRepositoryId)
                    .SetProperty(r => r.NodeId, incoming.NodeId)
                    .SetProperty(r => r.IsFork, incoming.IsFork)
                    .SetProperty(r => r.ParentCloneUrl, parentCloneUrlB));
            updateCount++;
        }

        // ---------------------------------------------------------------------------
        // PHASE C - Rename heuristic for still-unmatched pairs (same org URL-prefix, different repo name)
        //           Crucially: remove each matched old row from the candidate pool before continuing,
        //           preventing the same existing row from matching multiple incoming repos.
        // ---------------------------------------------------------------------------
        var unmatchedExisting = existing
            .Where(r => !matchedExistingIds.Contains(r.RepositoryId))
            .ToList(); // mutable list; we remove entries as they are consumed

        for (var i = 0; i < normalized.Count; i++)
        {
            if (matchedIncomingIndexes.Contains(i)) continue;
            var incoming = normalized[i];

            Repository? matched = null;
            for (var j = 0; j < unmatchedExisting.Count; j++)
            {
                if (IsLikelyRename(unmatchedExisting[j], incoming))
                {
                    matched = unmatchedExisting[j];
                    unmatchedExisting.RemoveAt(j); // consume so it cannot be reused
                    break;
                }
            }

            if (matched == null) continue;

            matchedExistingIds.Add(matched.RepositoryId);
            matchedIncomingIndexes.Add(i);

            renames.Add(new RenamedRepositoryInfo
            {
                OldName = matched.RepositoryName,
                NewName = incoming.RepositoryName,
                OrgName = matched.OrgName
            });
            _logger.LogInformation(
                "MergeRepositories: Rename detected via URL heuristic. RepositoryId={RepositoryId}, OldName={OldName}, NewName={NewName}, OldCloneUrl={OldCloneUrl}, NewCloneUrl={NewCloneUrl}, ConnectorId={ConnectorId}, OrgName={OrgName}",
                matched.RepositoryId, matched.RepositoryName, incoming.RepositoryName, matched.CloneUrl, incoming.CloneUrl, matched.ConnectorId, matched.OrgName);

            var parentCloneUrlC = ResolveParentCloneUrl(matched, incoming);
            await _dbContext.Repositories
                .Where(r => r.RepositoryId == matched.RepositoryId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.RepositoryName, incoming.RepositoryName)
                    .SetProperty(r => r.CloneUrl, incoming.CloneUrl)
                    .SetProperty(r => r.OrgName, incoming.OrgName)
                    .SetProperty(r => r.Visibility, incoming.Visibility)
                    .SetProperty(r => r.Archived, incoming.Archived)
                    .SetProperty(r => r.Topics, incoming.Topics)
                    .SetProperty(r => r.GitHubRepositoryId, incoming.GitHubRepositoryId)
                    .SetProperty(r => r.NodeId, incoming.NodeId)
                    .SetProperty(r => r.IsFork, incoming.IsFork)
                    .SetProperty(r => r.ParentCloneUrl, parentCloneUrlC));
            updateCount++;
        }

        // ---------------------------------------------------------------------------
        // PHASE D - Insert genuinely new repositories (no match found in any phase)
        // ---------------------------------------------------------------------------
        for (var i = 0; i < normalized.Count; i++)
        {
            if (matchedIncomingIndexes.Contains(i)) continue;
            var incoming = normalized[i];
            _dbContext.Repositories.Add(incoming);
            insertCount++;
        }

        if (insertCount > 0)
            await _dbContext.SaveChangesAsync();

        // A repository the provider returned again is no longer missing.
        if (matchedExistingIds.Count > 0)
        {
            var returnedAgainIds = existing
                .Where(r => r.MissingFromProviderSinceUtc != null && matchedExistingIds.Contains(r.RepositoryId))
                .Select(r => r.RepositoryId)
                .ToList();
            if (returnedAgainIds.Count > 0)
            {
                await _dbContext.Repositories
                    .Where(r => returnedAgainIds.Contains(r.RepositoryId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.MissingFromProviderSinceUtc, (DateTime?)null));
            }
        }

        // ---------------------------------------------------------------------------
        // PHASE E - Existing rows no longer returned by the provider.
        //           Only a connector that returned at least one repository in this merge has said anything
        //           about its repositories. A connector with nothing in the fetched set (failed, inactive,
        //           removed, or an empty answer) keeps every stored row.
        //           A repository linked to a Workspace is never deleted here: connector state must not remove
        //           a Workspace's repository (and with it the Feature rows that cascade from the link). It is
        //           kept, marked with MissingFromProviderSinceUtc and reported.
        //           Only catalog rows no Workspace links are deleted.
        // ---------------------------------------------------------------------------
        var answeredConnectorIds = normalized.Select(r => r.ConnectorId).ToHashSet();
        var candidates = existing
            .Where(r => !matchedExistingIds.Contains(r.RepositoryId))
            .Where(r => answeredConnectorIds.Contains(r.ConnectorId))
            .ToList();
        var candidateIds = candidates.Select(r => r.RepositoryId).ToList();

        var keptMissing = new List<KeptRepositoryInfo>();
        var toDeleteIds = candidateIds;

        if (candidateIds.Count > 0)
        {
            var candidateLinks = await _dbContext.WorkspaceRepositories
                .Where(wr => candidateIds.Contains(wr.RepositoryId))
                .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, WorkspaceName = wr.Workspace!.Name })
                .ToListAsync();
            var candidateLinkIds = candidateLinks.Select(l => l.WorkspaceRepositoryId).ToList();

            var featureNamesByLinkId = (await _dbContext.WorkspaceFeatureRepositories
                    .Where(wfr => candidateLinkIds.Contains(wfr.WorkspaceRepositoryId))
                    .Select(wfr => new { wfr.WorkspaceRepositoryId, FeatureName = wfr.WorkspaceFeatureContext!.WorkspaceFeature!.Name })
                    .ToListAsync())
                .ToLookup(row => row.WorkspaceRepositoryId, row => row.FeatureName);

            var linksByRepositoryId = candidateLinks.ToLookup(l => l.RepositoryId);
            var linkedRepositoryIds = candidateLinks.Select(l => l.RepositoryId).ToHashSet();
            toDeleteIds = candidateIds.Where(id => !linkedRepositoryIds.Contains(id)).ToList();

            var newlyMissingIds = new List<int>();
            foreach (var kept in candidates.Where(r => linkedRepositoryIds.Contains(r.RepositoryId)))
            {
                var links = linksByRepositoryId[kept.RepositoryId].ToList();
                var workspaceNames = links.Select(l => l.WorkspaceName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var featureNames = links.SelectMany(l => featureNamesByLinkId[l.WorkspaceRepositoryId]).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (kept.MissingFromProviderSinceUtc == null)
                {
                    newlyMissingIds.Add(kept.RepositoryId);
                    _logger.LogWarning(
                        "MergeRepositories: Repository {RepositoryId} ({RepositoryName}) is no longer returned by the provider but is linked to Workspace(s) {WorkspaceNames}; kept.",
                        kept.RepositoryId, kept.RepositoryName, string.Join(", ", workspaceNames));
                }

                keptMissing.Add(new KeptRepositoryInfo
                {
                    RepositoryId = kept.RepositoryId,
                    RepositoryName = kept.RepositoryName,
                    OrgName = kept.OrgName,
                    WorkspaceNames = workspaceNames,
                    FeatureNames = featureNames
                });
            }

            if (newlyMissingIds.Count > 0)
            {
                var missingSince = DateTime.UtcNow;
                await _dbContext.Repositories
                    .Where(r => newlyMissingIds.Contains(r.RepositoryId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.MissingFromProviderSinceUtc, (DateTime?)missingSince));
            }

            if (toDeleteIds.Count > 0)
            {
                foreach (var rid in toDeleteIds)
                    _logger.LogWarning(
                        "MergeRepositories: Repository no longer returned by provider and not linked to any Workspace, will delete. RepositoryId={RepositoryId}, RepositoryName={RepositoryName}",
                        rid,
                        existing.First(r => r.RepositoryId == rid).RepositoryName);

                // No Workspace links these rows, so there are no link dependents; repository-level dependents go first.
                await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(_dbContext, [], toDeleteIds);

                await _dbContext.Repositories
                    .Where(r => toDeleteIds.Contains(r.RepositoryId))
                    .ExecuteDeleteAsync();
            }
        }

        // A removed connector whose last repository just moved to another connector (Phase B) is purged here.
        await RemovedConnectorCleanup.PurgeUnusedAsync(_dbContext);

        await transaction.CommitAsync();

        _logger.LogInformation(
            "MergeRepositories: Complete. Updated={Updated}, Inserted={Inserted}, Deleted={Deleted}, Renames={Renames}, KeptMissingFromProvider={KeptMissingFromProvider}",
            updateCount, insertCount, toDeleteIds.Count, renames.Count, keptMissing.Count);

        return new MergeRepositoriesResult
        {
            Renames = renames,
            MergedRepositoryIdMap = new Dictionary<int, int>(),
            KeptMissingFromProvider = keptMissing
        };
    }

    /// <summary>
    /// Parent clone URL to persist for <paramref name="incoming"/>: none for a non-fork; for a fork the freshly resolved URL, falling back to
    /// the stored one when this fetch could not resolve it (a failed parent lookup must not erase what is already known).
    /// </summary>
    private static string? ResolveParentCloneUrl(Repository existing, Repository incoming)
    {
        if (!incoming.IsFork)
            return null;
        return string.IsNullOrWhiteSpace(incoming.ParentCloneUrl) ? existing.ParentCloneUrl : incoming.ParentCloneUrl;
    }

    private static bool IsLikelyRename(Repository oldRepo, Repository newRepo)
    {
        if (oldRepo.ConnectorId != newRepo.ConnectorId) return false;
        if (!string.Equals(oldRepo.OrgName, newRepo.OrgName, StringComparison.OrdinalIgnoreCase)) return false;

        if (!Uri.TryCreate(oldRepo.CloneUrl, UriKind.Absolute, out var oldUri) ||
            !Uri.TryCreate(newRepo.CloneUrl, UriKind.Absolute, out var newUri))
            return false;

        if (!string.Equals(oldUri.Scheme, newUri.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(oldUri.Host, newUri.Host, StringComparison.OrdinalIgnoreCase)) return false;

        var oldPath = oldUri.AbsolutePath.TrimEnd('/');
        var newPath = newUri.AbsolutePath.TrimEnd('/');

        if (oldPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) oldPath = oldPath[..^4];
        if (newPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) newPath = newPath[..^4];

        var oldSlash = oldPath.LastIndexOf('/');
        var newSlash = newPath.LastIndexOf('/');
        if (oldSlash < 0 || newSlash < 0) return false;

        // Same owner/org path prefix, only the trailing repo-name segment differs
        return string.Equals(oldPath[..oldSlash], newPath[..newSlash], StringComparison.OrdinalIgnoreCase)
               && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
    }
}
