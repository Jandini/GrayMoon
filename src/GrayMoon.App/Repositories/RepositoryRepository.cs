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
                CloneUrl = repository.CloneUrl,
                Topics = repository.Topics
            })
            .ToListAsync();
    }

    /// <summary>
    /// Merges fetched repositories with existing ones using <see cref="Repository.CloneUrl"/> as the unique key.
    /// Detects GitHub renames (same connector, org and URL owner-prefix, only the name segment differs) and updates
    /// the existing row in place — preserving <see cref="Repository.RepositoryId"/> and all workspace links.
    /// Adds new rows for genuinely new clone URLs and removes rows no longer returned by GitHub.
    /// </summary>
    /// <returns>Repositories that were detected as renames and updated in place.</returns>
    public async Task<IReadOnlyList<RenamedRepositoryInfo>> MergeRepositoriesAsync(IReadOnlyCollection<Repository> repositories)
    {
        var normalized = repositories
            .Select(r => new Repository
            {
                ConnectorId = r.ConnectorId,
                OrgName = r.OrgName,
                RepositoryName = r.RepositoryName,
                Visibility = r.Visibility,
                CloneUrl = (r.CloneUrl ?? string.Empty).Trim(),
                Topics = r.Topics
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.CloneUrl))
            .GroupBy(r => r.CloneUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var fetchedUrls = new HashSet<string>(normalized.Select(r => r.CloneUrl), StringComparer.OrdinalIgnoreCase);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var existing = await _dbContext.Repositories.AsNoTracking().ToListAsync();
        var toRemove = existing.Where(r => !fetchedUrls.Contains(r.CloneUrl.Trim())).ToList();

        // Identify new repos (fetched URLs not yet in DB) — candidates for rename targets
        var existingUrls = new HashSet<string>(existing.Select(r => r.CloneUrl.Trim()), StringComparer.OrdinalIgnoreCase);
        var newRepos = normalized.Where(r => !existingUrls.Contains(r.CloneUrl)).ToList();

        // Detect renames: match removed repos to new repos by same connector + org + URL owner-prefix
        var renames = new List<RenamedRepositoryInfo>();
        var renamedOldUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedNewUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oldRepo in toRemove)
        {
            foreach (var newRepo in newRepos)
            {
                if (renamedNewUrls.Contains(newRepo.CloneUrl)) continue;
                if (IsLikelyRename(oldRepo, newRepo))
                {
                    renames.Add(new RenamedRepositoryInfo
                    {
                        OldName = oldRepo.RepositoryName,
                        NewName = newRepo.RepositoryName,
                        OrgName = oldRepo.OrgName
                    });
                    renamedOldUrls.Add(oldRepo.CloneUrl.Trim());
                    renamedNewUrls.Add(newRepo.CloneUrl);
                    _logger.LogInformation(
                        "Detected GitHub rename: '{OldName}' → '{NewName}' (ConnectorId={ConnectorId}, Org={OrgName})",
                        oldRepo.RepositoryName, newRepo.RepositoryName, oldRepo.ConnectorId, oldRepo.OrgName);
                    break;
                }
            }
        }

        // Repos to actually delete = toRemove minus those identified as renames
        var actualRemovals = toRemove.Where(r => !renamedOldUrls.Contains(r.CloneUrl.Trim())).ToList();
        if (actualRemovals.Count > 0)
        {
            // Load the rows into the tracker so EF Core can issue the DELETEs
            var removalIds = actualRemovals.Select(r => r.RepositoryId).ToHashSet();
            var trackedRemovals = await _dbContext.Repositories
                .Where(r => removalIds.Contains(r.RepositoryId))
                .ToListAsync();
            _dbContext.Repositories.RemoveRange(trackedRemovals);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: Repository. Action=Merge (remove not fetched), RemovedCount={RemovedCount}", actualRemovals.Count);
        }

        // Apply updates and inserts; for renames, UPDATE the existing row in place (preserves RepositoryId + workspace links)
        var existingByUrl = existing
            .Where(r => fetchedUrls.Contains(r.CloneUrl.Trim()) && !renamedOldUrls.Contains(r.CloneUrl.Trim()))
            .GroupBy(r => r.CloneUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Map renamed old repo by old URL so we can load it for in-place update
        var renameByOldUrl = toRemove
            .Where(r => renamedOldUrls.Contains(r.CloneUrl.Trim()))
            .ToDictionary(r => r.CloneUrl.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var repo in normalized)
        {
            if (renamedNewUrls.Contains(repo.CloneUrl))
            {
                // Find the existing row that this new repo renames, load it tracked, then update in place
                var matchingOldRepo = renameByOldUrl.Values.FirstOrDefault(old => IsLikelyRename(old, repo));
                if (matchingOldRepo != null)
                {
                    var trackedExisting = await _dbContext.Repositories.FindAsync(matchingOldRepo.RepositoryId);
                    if (trackedExisting != null)
                    {
                        trackedExisting.RepositoryName = repo.RepositoryName;
                        trackedExisting.CloneUrl = repo.CloneUrl;
                        trackedExisting.OrgName = repo.OrgName;
                        trackedExisting.Visibility = repo.Visibility;
                        trackedExisting.Topics = repo.Topics;
                    }
                }
            }
            else if (existingByUrl.TryGetValue(repo.CloneUrl, out var existingEntry))
            {
                var trackedExisting = await _dbContext.Repositories.FindAsync(existingEntry.RepositoryId);
                if (trackedExisting != null)
                {
                    trackedExisting.ConnectorId = repo.ConnectorId;
                    trackedExisting.OrgName = repo.OrgName;
                    trackedExisting.RepositoryName = repo.RepositoryName;
                    trackedExisting.Visibility = repo.Visibility;
                    trackedExisting.CloneUrl = repo.CloneUrl;
                    trackedExisting.Topics = repo.Topics;
                }
            }
            else
            {
                await _dbContext.Repositories.AddAsync(repo);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation(
            "Persistence: Repository. Action=Merge, TotalFetched={TotalFetched}, Renames={Renames}, Removed={Removed}",
            normalized.Count, renames.Count, actualRemovals.Count);
        await transaction.CommitAsync();

        return renames;
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
