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
                CloneUrl = repository.CloneUrl
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
                CloneUrl = repository.CloneUrl
            })
            .ToListAsync();
    }

    /// <summary>
    /// Merges fetched repositories with existing ones using <see cref="Repository.CloneUrl"/> as the unique key.
    /// Updates existing rows when CloneUrl matches; adds new rows for new CloneUrls.
    /// Removes repositories that are not in <paramref name="repositories"/> (and their workspace links via cascade).
    /// </summary>
    public async Task MergeRepositoriesAsync(IReadOnlyCollection<Repository> repositories)
    {
        var normalized = repositories
            .Select(r => new Repository
            {
                ConnectorId = r.ConnectorId,
                OrgName = r.OrgName,
                RepositoryName = r.RepositoryName,
                Visibility = r.Visibility,
                CloneUrl = (r.CloneUrl ?? string.Empty).Trim()
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.CloneUrl))
            .GroupBy(r => r.CloneUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var fetchedUrls = new HashSet<string>(normalized.Select(r => r.CloneUrl), StringComparer.OrdinalIgnoreCase);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var existing = await _dbContext.Repositories.ToListAsync();
        var toRemove = existing.Where(r => !fetchedUrls.Contains(r.CloneUrl.Trim())).ToList();
        if (toRemove.Count > 0)
        {
            _dbContext.Repositories.RemoveRange(toRemove);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: Repository. Action=Merge (remove not fetched), RemovedCount={RemovedCount}", toRemove.Count);
        }

        var existingByUrl = existing
            .Where(r => fetchedUrls.Contains(r.CloneUrl.Trim()))
            .GroupBy(r => r.CloneUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var repo in normalized)
        {
            if (existingByUrl.TryGetValue(repo.CloneUrl, out var existingRepo))
            {
                existingRepo.ConnectorId = repo.ConnectorId;
                existingRepo.OrgName = repo.OrgName;
                existingRepo.RepositoryName = repo.RepositoryName;
                existingRepo.Visibility = repo.Visibility;
                existingRepo.CloneUrl = repo.CloneUrl;
            }
            else
            {
                await _dbContext.Repositories.AddAsync(repo);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: Repository. Action=Merge, TotalFetched={TotalFetched}, UpdatedOrAdded={UpdatedOrAdded}", normalized.Count, normalized.Count);
        await transaction.CommitAsync();
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
