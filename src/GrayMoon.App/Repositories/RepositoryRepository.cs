using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class GitHubRepositoryRepository(AppDbContext dbContext, ILogger<GitHubRepositoryRepository> logger)
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
                ConnectorName = repository.Connector != null ? repository.Connector.ConnectorName : "Unknown",
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                Visibility = repository.Visibility,
                Archived = repository.Archived,
                CloneUrl = repository.CloneUrl,
                Topics = repository.Topics
            })
            .ToListAsync();
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
                ConnectorName = repository.Connector != null ? repository.Connector.ConnectorName : "Unknown",
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
    }

    /// <summary>
    /// Merges fetched repositories with existing ones.
    /// Matching priority: (1) stable provider ID (ConnectorId + GitHubRepositoryId), (2) CloneUrl, (3) URL-prefix rename heuristic.
    /// Updates existing rows in-place - preserving RepositoryId and all workspace links.
    /// Adds new rows for genuinely new repositories, removes rows no longer returned by GitHub.
    /// Handles multiple simultaneous renames in a single pass.
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
                || !string.Equals(match.NodeId, incoming.NodeId, StringComparison.Ordinal))
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

                await _dbContext.Repositories
                    .Where(r => r.RepositoryId == match.RepositoryId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.RepositoryName, incoming.RepositoryName)
                        .SetProperty(r => r.CloneUrl, incoming.CloneUrl)
                        .SetProperty(r => r.OrgName, incoming.OrgName)
                        .SetProperty(r => r.Visibility, incoming.Visibility)
                    .SetProperty(r => r.Archived, incoming.Archived)
                        .SetProperty(r => r.Topics, incoming.Topics)
                        .SetProperty(r => r.NodeId, incoming.NodeId));
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
                    .SetProperty(r => r.NodeId, incoming.NodeId));
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
                    .SetProperty(r => r.NodeId, incoming.NodeId));
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

        // ---------------------------------------------------------------------------
        // PHASE E - Delete unmatched existing rows (no longer returned by the provider)
        //           Delete WorkspaceRepositoryLink rows first (ExecuteDeleteAsync respects no tracker).
        // ---------------------------------------------------------------------------
        var toDeleteIds = existing
            .Where(r => !matchedExistingIds.Contains(r.RepositoryId))
            .Select(r => r.RepositoryId)
            .ToList();

        if (toDeleteIds.Count > 0)
        {
            foreach (var rid in toDeleteIds)
                _logger.LogWarning(
                    "MergeRepositories: Repository no longer returned by provider, will delete. RepositoryId={RepositoryId}, RepositoryName={RepositoryName}",
                    rid,
                    existing.First(r => r.RepositoryId == rid).RepositoryName);

            // Delete dependent rows first so FK constraint is not violated.
            await _dbContext.WorkspaceRepositories
                .Where(wr => toDeleteIds.Contains(wr.RepositoryId))
                .ExecuteDeleteAsync();

            await _dbContext.Repositories
                .Where(r => toDeleteIds.Contains(r.RepositoryId))
                .ExecuteDeleteAsync();
        }

        await transaction.CommitAsync();

        _logger.LogInformation(
            "MergeRepositories: Complete. Updated={Updated}, Inserted={Inserted}, Deleted={Deleted}, Renames={Renames}",
            updateCount, insertCount, toDeleteIds.Count, renames.Count);

        return new MergeRepositoriesResult
        {
            Renames = renames,
            MergedRepositoryIdMap = new Dictionary<int, int>()
        };
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

    public async Task DeleteOrphanedAsync(IReadOnlyCollection<int> connectorIds)
    {
        if (connectorIds.Count == 0)
        {
            var allCount = await _dbContext.Repositories.CountAsync();
            _dbContext.Repositories.RemoveRange(_dbContext.Repositories);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved Repository. Action=DeleteOrphaned, RemovedCount={RemovedCount} (all; no connectors)", allCount);
            return;
        }

        var orphans = await _dbContext.Repositories
            .Where(repository => !connectorIds.Contains(repository.ConnectorId))
            .ToListAsync();
        var removedCount = orphans.Count;
        _dbContext.Repositories.RemoveRange(orphans);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Repository. Action=DeleteOrphaned, RemovedCount={RemovedCount}", removedCount);
    }
}
