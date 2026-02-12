using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class GitHubRepositoryService(
    ConnectorRepository connectorRepository,
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

        var result = await RefreshRepositoriesAsync();
        return result.Repositories.ToList();
    }

    public async Task<List<GitHubRepositoryEntry>> GetPersistedRepositoriesAsync()
    {
        return await repositoryRepository.GetAllEntriesAsync();
    }

    public async Task<RefreshRepositoriesResult> RefreshRepositoriesAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("User triggered fetch repositories from GitHub");
        var allFetched = new List<Repository>();
        var connectorErrors = new List<ConnectorFetchError>();
        var uniqueCloneUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectors = await connectorRepository.GetAllAsync();
        var activeConnectors = connectors.Where(connector => connector.IsActive && connector.ConnectorType == ConnectorType.GitHub).ToList();

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
                var persisted = repositories.Select(repo => new Repository
                {
                    ConnectorId = connector.ConnectorId,
                    OrgName = repo.Owner?.Login,
                    RepositoryName = repo.Name,
                    Visibility = repo.Private ? "Private" : "Public",
                    CloneUrl = (repo.CloneUrl ?? string.Empty).Trim(),
                    Topics = repo.Topics != null && repo.Topics.Count > 0
                        ? string.Join(",", repo.Topics.Where(t => !string.IsNullOrWhiteSpace(t)))
                        : null
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
                var message = ex is InvalidOperationException ? ex.Message : "Failed to fetch repositories. Please check the connector configuration.";
                connectorErrors.Add(new ConnectorFetchError { ConnectorName = connector.ConnectorName, Message = message });
                await connectorRepository.UpdateStatusAsync(connector.ConnectorId, "Error", message);
            }
        }

        if (allFetched.Count > 0)
        {
            await repositoryRepository.MergeRepositoriesAsync(allFetched);
        }

        var repositoriesList = await repositoryRepository.GetAllEntriesAsync();
        return new RefreshRepositoriesResult { Repositories = repositoriesList, ConnectorErrors = connectorErrors };
    }
}
