using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitHub;

public sealed class GitHubRepositoryService(
    ConnectorRepository connectorRepository,
    GitHubRepositoryRepository repositoryRepository,
    GitHubService gitHubService,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<GitHubRepositoryService> logger)
{
    public async Task<List<GitHubRepositoryEntry>> GetRepositoriesAsync()
    {
        var persisted = await repositoryRepository.GetAllEntriesAsync();
        if (persisted.Count > 0)
        {
            return persisted;
        }

        await RefreshRepositoriesAsync();
        return await repositoryRepository.GetAllEntriesAsync();
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

        // Connectors are fetched in parallel, so every touch of the shared clone-url set and of the progress
        // callback goes through one lock.
        var sharedStateLock = new object();
        var batchProgress = progress == null ? null : new Progress<IReadOnlyList<GitHubRepositoryDto>>(batch =>
        {
            lock (sharedStateLock)
            {
                foreach (var repo in batch)
                {
                    var url = (repo.CloneUrl ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(url))
                        uniqueCloneUrls.Add(url);
                }
                progress.Report(uniqueCloneUrls.Count);
            }
        });

        using var gate = new SemaphoreSlim(Math.Max(1, workspaceOptions.Value.MaxParallelOperations));
        var fetches = await Task.WhenAll(activeConnectors.Select(connector =>
            FetchConnectorAsync(connector, gate, batchProgress, sharedStateLock, uniqueCloneUrls, progress, cancellationToken)));

        // Apply the results in connector order. The status writes share this service's scoped DbContext, so they stay sequential.
        for (var i = 0; i < activeConnectors.Count; i++)
        {
            var connector = activeConnectors[i];
            var fetch = fetches[i];
            if (fetch.ErrorMessage == null)
            {
                allFetched.AddRange(fetch.Repositories);
                continue;
            }

            connectorErrors.Add(new ConnectorFetchError { ConnectorName = connector.ConnectorName, Message = fetch.ErrorMessage });
            await connectorRepository.UpdateStatusAsync(connector.ConnectorId, "Error", fetch.ErrorMessage);
        }

        IReadOnlyList<RenamedRepositoryInfo> renamedRepositories = [];
        IReadOnlyDictionary<int, int> mergedIdMap = new Dictionary<int, int>();
        IReadOnlyList<KeptRepositoryForFeaturesInfo> keptForFeatures = [];
        if (allFetched.Count > 0)
        {
            var mergeResult = await repositoryRepository.MergeRepositoriesAsync(allFetched);
            renamedRepositories = mergeResult.Renames;
            mergedIdMap = mergeResult.MergedRepositoryIdMap;
            keptForFeatures = mergeResult.KeptForFeatures;
        }

        return new RefreshRepositoriesResult
        {
            ConnectorErrors = connectorErrors,
            RenamedRepositories = renamedRepositories,
            MergedRepositoryIdMap = mergedIdMap,
            KeptForFeatures = keptForFeatures
        };
    }

    private async Task<ConnectorFetch> FetchConnectorAsync(
        Connector connector,
        SemaphoreSlim gate,
        IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress,
        object sharedStateLock,
        HashSet<string> uniqueCloneUrls,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var repositories = await gitHubService.GetRepositoriesAsync(connector, progress: null, batchProgress, cancellationToken);
            var persisted = repositories.Select(repo => new Repository
            {
                ConnectorId = connector.ConnectorId,
                OrgName = repo.Owner?.Login,
                RepositoryName = repo.Name,
                Visibility = repo.Private ? "Private" : "Public",
                Archived = repo.Archived,
                CloneUrl = (repo.CloneUrl ?? string.Empty).Trim(),
                GitHubRepositoryId = repo.Id,
                NodeId = string.IsNullOrWhiteSpace(repo.NodeId) ? null : repo.NodeId,
                Topics = repo.Topics != null && repo.Topics.Count > 0
                    ? string.Join(",", repo.Topics.Where(t => !string.IsNullOrWhiteSpace(t)))
                    : null
            }).ToList();

            lock (sharedStateLock)
            {
                foreach (var r in persisted)
                {
                    if (!string.IsNullOrWhiteSpace(r.CloneUrl))
                        uniqueCloneUrls.Add(r.CloneUrl);
                }
                progress?.Report(uniqueCloneUrls.Count);
            }

            return new ConnectorFetch(persisted, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading repositories for connector {ConnectorName}", connector.ConnectorName);
            var message = ex is InvalidOperationException ? ex.Message : "Failed to fetch repositories. Please check the connector configuration.";
            return new ConnectorFetch([], message);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record ConnectorFetch(List<Repository> Repositories, string? ErrorMessage);
}
