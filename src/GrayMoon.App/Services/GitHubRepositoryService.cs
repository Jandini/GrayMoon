using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class GitHubRepositoryService(
    GitHubConnectorRepository connectorRepository,
    GitHubRepositoryRepository repositoryRepository,
    GitHubService gitHubService,
    ILogger<GitHubRepositoryService> logger)
{
    public async Task<List<GitHubRepositoryEntry>> GetRepositoriesAsync()
    {
        var persisted = await repositoryRepository.GetAllEntriesAsync();
        if (persisted.Count > 0)
        {
            return persisted;
        }

        return await RefreshRepositoriesAsync();
    }

    public async Task<List<GitHubRepositoryEntry>> GetPersistedRepositoriesAsync()
    {
        return await repositoryRepository.GetAllEntriesAsync();
    }

    public async Task<List<GitHubRepositoryEntry>> RefreshRepositoriesAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("User triggered fetch repositories from GitHub");
        var allFetched = new List<GitHubRepository>();
        var uniqueCloneUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectors = await connectorRepository.GetAllAsync();
        var activeConnectors = connectors.Where(connector => connector.IsActive).ToList();

        progress?.Report(0);

        var batchProgress = progress == null ? null : new Progress<IReadOnlyList<GitHubRepositoryDto>>(batch =>
        {
            foreach (var repo in batch)
            {
                var url = (repo.CloneUrl ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    uniqueCloneUrls.Add(url);
            }
            progress.Report(uniqueCloneUrls.Count);
        });

        foreach (var connector in activeConnectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var repositories = await gitHubService.GetRepositoriesAsync(connector, progress: null, batchProgress, cancellationToken);
                var persisted = repositories.Select(repo => new GitHubRepository
                {
                    GitHubConnectorId = connector.GitHubConnectorId,
                    OrgName = repo.Owner?.Login,
                    RepositoryName = repo.Name,
                    Visibility = repo.Private ? "Private" : "Public",
                    CloneUrl = (repo.CloneUrl ?? string.Empty).Trim()
                }).ToList();
                allFetched.AddRange(persisted);
                foreach (var r in persisted)
                {
                    if (!string.IsNullOrWhiteSpace(r.CloneUrl))
                        uniqueCloneUrls.Add(r.CloneUrl);
                }
                progress?.Report(uniqueCloneUrls.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading repositories for connector {ConnectorName}", connector.ConnectorName);
            }
        }

        if (allFetched.Count > 0)
        {
            await repositoryRepository.MergeRepositoriesAsync(allFetched);
        }

        return await repositoryRepository.GetAllEntriesAsync();
    }
}
