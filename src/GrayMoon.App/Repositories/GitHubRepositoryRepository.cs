using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class GitHubRepositoryRepository
{
    private readonly AppDbContext _dbContext;

    public GitHubRepositoryRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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

        var existing = _dbContext.GitHubRepositories
            .Where(repository => repository.GitHubConnectorId == connectorId);

        _dbContext.GitHubRepositories.RemoveRange(existing);
        await _dbContext.SaveChangesAsync();

        if (repositories.Count > 0)
        {
            await _dbContext.GitHubRepositories.AddRangeAsync(repositories);
            await _dbContext.SaveChangesAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task DeleteOrphanedAsync(IReadOnlyCollection<int> connectorIds)
    {
        if (connectorIds.Count == 0)
        {
            _dbContext.GitHubRepositories.RemoveRange(_dbContext.GitHubRepositories);
            await _dbContext.SaveChangesAsync();
            return;
        }

        var orphans = _dbContext.GitHubRepositories
            .Where(repository => !connectorIds.Contains(repository.GitHubConnectorId));

        _dbContext.GitHubRepositories.RemoveRange(orphans);
        await _dbContext.SaveChangesAsync();
    }
}
