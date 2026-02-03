using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class GitHubRepositoryRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<GitHubRepositoryRepository> _logger;

    public GitHubRepositoryRepository(AppDbContext dbContext, ILogger<GitHubRepositoryRepository> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<GitHubRepositoryEntry>> GetAllEntriesAsync()
    {
        return await _dbContext.GitHubRepositories
            .AsNoTracking()
            .Include(repository => repository.GitHubConnector)
            .OrderBy(repository => repository.RepositoryName)
            .Select(repository => new GitHubRepositoryEntry
            {
                GitHubRepositoryId = repository.GitHubRepositoryId,
                ConnectorName = repository.GitHubConnector != null ? repository.GitHubConnector.ConnectorName : "Unknown",
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                Visibility = repository.Visibility,
                CloneUrl = repository.CloneUrl
            })
            .ToListAsync();
    }

    public async Task<GitHubRepository?> GetByIdAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.GitHubRepositories
            .AsNoTracking()
            .Include(repository => repository.GitHubConnector)
            .FirstOrDefaultAsync(r => r.GitHubRepositoryId == repositoryId, cancellationToken);
    }

    public async Task<GitHubRepository?> GetByCloneUrlAsync(string cloneUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return null;
        return await _dbContext.GitHubRepositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CloneUrl == cloneUrl.Trim(), cancellationToken);
    }

    public async Task<List<GitHubRepositoryEntry>> GetEntriesByConnectorIdAsync(int connectorId)
    {
        return await _dbContext.GitHubRepositories
            .AsNoTracking()
            .Include(repository => repository.GitHubConnector)
            .Where(repository => repository.GitHubConnectorId == connectorId)
            .OrderBy(repository => repository.RepositoryName)
            .Select(repository => new GitHubRepositoryEntry
            {
                GitHubRepositoryId = repository.GitHubRepositoryId,
                ConnectorName = repository.GitHubConnector != null ? repository.GitHubConnector.ConnectorName : "Unknown",
                OrgName = repository.OrgName,
                RepositoryName = repository.RepositoryName,
                Visibility = repository.Visibility,
                CloneUrl = repository.CloneUrl
            })
            .ToListAsync();
    }

    /// <summary>
    /// Merges fetched repositories with existing ones using <see cref="GitHubRepository.CloneUrl"/> as the unique key.
    /// Updates existing rows when CloneUrl matches; adds new rows for new CloneUrls.
    /// Removes repositories that are not in <paramref name="repositories"/> (and their workspace links via cascade).
    /// </summary>
    public async Task MergeRepositoriesAsync(IReadOnlyCollection<GitHubRepository> repositories)
    {
        var normalized = repositories
            .Select(r => new GitHubRepository
            {
                GitHubConnectorId = r.GitHubConnectorId,
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

        var existing = await _dbContext.GitHubRepositories.ToListAsync();
        var toRemove = existing.Where(r => !fetchedUrls.Contains(r.CloneUrl.Trim())).ToList();
        if (toRemove.Count > 0)
        {
            _dbContext.GitHubRepositories.RemoveRange(toRemove);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: GitHubRepository. Action=Merge (remove not fetched), RemovedCount={RemovedCount}", toRemove.Count);
        }

        var existingByUrl = existing
            .Where(r => fetchedUrls.Contains(r.CloneUrl.Trim()))
            .GroupBy(r => r.CloneUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var repo in normalized)
        {
            if (existingByUrl.TryGetValue(repo.CloneUrl, out var existingRepo))
            {
                existingRepo.GitHubConnectorId = repo.GitHubConnectorId;
                existingRepo.OrgName = repo.OrgName;
                existingRepo.RepositoryName = repo.RepositoryName;
                existingRepo.Visibility = repo.Visibility;
                existingRepo.CloneUrl = repo.CloneUrl;
            }
            else
            {
                await _dbContext.GitHubRepositories.AddAsync(repo);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: GitHubRepository. Action=Merge, TotalFetched={TotalFetched}, UpdatedOrAdded={UpdatedOrAdded}", normalized.Count, normalized.Count);
        await transaction.CommitAsync();
    }

    public async Task DeleteOrphanedAsync(IReadOnlyCollection<int> connectorIds)
    {
        if (connectorIds.Count == 0)
        {
            var allCount = await _dbContext.GitHubRepositories.CountAsync();
            _dbContext.GitHubRepositories.RemoveRange(_dbContext.GitHubRepositories);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved GitHubRepository. Action=DeleteOrphaned, RemovedCount={RemovedCount} (all; no connectors)", allCount);
            return;
        }

        var orphans = await _dbContext.GitHubRepositories
            .Where(repository => !connectorIds.Contains(repository.GitHubConnectorId))
            .ToListAsync();
        var removedCount = orphans.Count;
        _dbContext.GitHubRepositories.RemoveRange(orphans);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved GitHubRepository. Action=DeleteOrphaned, RemovedCount={RemovedCount}", removedCount);
    }
}
