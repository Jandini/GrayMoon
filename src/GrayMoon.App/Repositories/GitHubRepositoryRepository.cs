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

    public async Task ReplaceForConnectorAsync(int connectorId, IReadOnlyCollection<GitHubRepository> repositories)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var existing = await _dbContext.GitHubRepositories
            .Where(repository => repository.GitHubConnectorId == connectorId)
            .ToListAsync();
        var removedCount = existing.Count;
        _dbContext.GitHubRepositories.RemoveRange(existing);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved GitHubRepository. Action=ReplaceForConnector (remove), ConnectorId={ConnectorId}, RemovedCount={RemovedCount}", connectorId, removedCount);

        if (repositories.Count > 0)
        {
            await _dbContext.GitHubRepositories.AddRangeAsync(repositories);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved GitHubRepository. Action=ReplaceForConnector (add), ConnectorId={ConnectorId}, AddedCount={AddedCount}", connectorId, repositories.Count);
        }

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
