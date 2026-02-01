using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class GitHubRepositoryService
{
    private readonly GitHubConnectorRepository _connectorRepository;
    private readonly GitHubRepositoryRepository _repositoryRepository;
    private readonly GitHubService _gitHubService;
    private readonly ILogger<GitHubRepositoryService> _logger;

    public GitHubRepositoryService(
        GitHubConnectorRepository connectorRepository,
        GitHubRepositoryRepository repositoryRepository,
        GitHubService gitHubService,
        ILogger<GitHubRepositoryService> logger)
    {
        _connectorRepository = connectorRepository;
        _repositoryRepository = repositoryRepository;
        _gitHubService = gitHubService;
        _logger = logger;
    }

    public async Task<List<GitHubRepositoryEntry>> GetRepositoriesAsync()
    {
        var persisted = await _repositoryRepository.GetAllEntriesAsync();
        if (persisted.Count > 0)
        {
            return persisted;
        }

        return await RefreshRepositoriesAsync();
    }

    public async Task<List<GitHubRepositoryEntry>> GetPersistedRepositoriesAsync()
    {
        return await _repositoryRepository.GetAllEntriesAsync();
    }

    public async Task<List<GitHubRepositoryEntry>> RefreshRepositoriesAsync()
    {
        _logger.LogInformation("User triggered fetch repositories from GitHub");
        var results = new List<GitHubRepositoryEntry>();
        var connectors = await _connectorRepository.GetAllAsync();
        var activeConnectors = connectors.Where(connector => connector.IsActive).ToList();
        var connectorIds = connectors.Select(connector => connector.GitHubConnectorId).ToList();
        await _repositoryRepository.DeleteOrphanedAsync(connectorIds);

        foreach (var connector in activeConnectors)
        {
            try
            {
                var repositories = await _gitHubService.GetRepositoriesAsync(connector);
                var persisted = repositories.Select(repo => new GitHubRepository
                {
                    GitHubConnectorId = connector.GitHubConnectorId,
                    OrgName = repo.Owner?.Login,
                    RepositoryName = repo.Name,
                    Visibility = repo.Private ? "Private" : "Public",
                    CloneUrl = repo.CloneUrl
                }).ToList();

                await _repositoryRepository.ReplaceForConnectorAsync(connector.GitHubConnectorId, persisted);

                var entries = await _repositoryRepository.GetEntriesByConnectorIdAsync(connector.GitHubConnectorId);
                results.AddRange(entries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading repositories for connector {ConnectorName}", connector.ConnectorName);
                results.AddRange(await _repositoryRepository.GetEntriesByConnectorIdAsync(connector.GitHubConnectorId));
            }
        }

        if (results.Count == 0)
        {
            return await _repositoryRepository.GetAllEntriesAsync();
        }

        return results;
    }
}
