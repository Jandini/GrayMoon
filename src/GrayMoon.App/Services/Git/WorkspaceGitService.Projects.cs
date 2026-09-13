using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>Refreshes project and package reference data from .csproj files on disk (no git). Merges into WorkspaceProjects and ProjectDependencies. When <paramref name="repositoryIds"/> is set, only those repos are refreshed.</summary>
    public async Task RefreshWorkspaceProjectsAsync(
        int workspaceId,
        Action<int, int, int>? onProgress = null,
        Action<int, string>? onRepoError = null,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to refresh projects.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var repos = workspace.Repositories
            .Select(link => link.Repository)
            .Where(r => r != null)
            .Cast<Repository>()
            .ToList();

        if (repositoryIds != null && repositoryIds.Count > 0)
            repos = repos.Where(r => repositoryIds.Contains(r.RepositoryId)).ToList();

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();
        if (tagPinnedIds.Count > 0)
            repos = repos.Where(r => !tagPinnedIds.Contains(r.RepositoryId)).ToList();

        if (repos.Count == 0)
        {
            _logger.LogInformation("RefreshWorkspaceProjects: no repositories for workspace {WorkspaceName}", workspace.Name);
            return;
        }

        _logger.LogInformation("RefreshWorkspaceProjects: Workspace={WorkspaceName}, RepoCount={RepoCount}", workspace.Name, repos.Count);

        var completedCount = 0;
        var totalCount = repos.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        var syncResults = await Task.WhenAll(repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot, maxParallelOperations = _maxConcurrent };
                var response = await _agentBridge.SendCommandAsync("RefreshRepositoryProjects", args, cancellationToken);
                if (!response.Success)
                {
                    onRepoError?.Invoke(repo.RepositoryId, response.Error ?? "Refresh projects failed");
                    var c = Interlocked.Increment(ref completedCount);
                    onProgress?.Invoke(c, totalCount, repo.RepositoryId);
                    return (repo.RepositoryId, ProjectsDetail: (IReadOnlyList<SyncProjectInfo>?)null);
                }
                var projectsDetail = response.Data != null ? GetProjectsDetail(response.Data) : null;
                var c2 = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(c2, totalCount, repo.RepositoryId);
                return (repo.RepositoryId, ProjectsDetail: projectsDetail);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        var repoProjectsToMerge = syncResults
            .Where(r => r.ProjectsDetail is { Count: > 0 })
            .Select(r => (r.RepositoryId, (IReadOnlyList<SyncProjectInfo>)r.ProjectsDetail!))
            .ToList();
        if (repoProjectsToMerge.Count > 0)
            await _workspaceProjectRepository.MergeWorkspaceProjectsBatchAsync(workspaceId, repoProjectsToMerge, cancellationToken);

        var repoIdsToUpdate = syncResults.Select(r => r.RepositoryId).ToList();
        var linksToUpdate = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIdsToUpdate.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);
        foreach (var r in syncResults)
        {
            var link = linksToUpdate.FirstOrDefault(l => l.RepositoryId == r.RepositoryId);
            if (link != null)
                link.RepositoryType = ComputeRepositoryType(r.ProjectsDetail);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        var resultsForDeps = syncResults.Select(r => (r.RepositoryId, r.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, resultsForDeps, persistDependencyLevel: true, cancellationToken);

        _logger.LogDebug("RefreshWorkspaceProjects completed for workspace {WorkspaceName}", workspace.Name);
    }

    /// <summary>Refreshes project and package reference data for a single repository. Merges into WorkspaceProjects and ProjectDependencies for that repo only, then recomputes dependency stats. Returns true if refresh succeeded.</summary>
    public async Task<bool> RefreshSingleRepositoryProjectsAsync(
        int workspaceId,
        int repositoryId,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to refresh projects.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var repo = workspace.Repositories.Select(l => l.Repository).FirstOrDefault(r => r != null && r.RepositoryId == repositoryId);
        if (repo == null)
            throw new InvalidOperationException($"Repository {repositoryId} not found in workspace.");

        var linkForWorkspace = workspace.Repositories.FirstOrDefault(l => l.RepositoryId == repositoryId);
        if (!string.IsNullOrWhiteSpace(linkForWorkspace?.CheckedOutTag))
            return false;

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var args = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot, maxParallelOperations = _maxConcurrent };
        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryProjects", args, cancellationToken);
        if (!response.Success)
        {
            onRepoError?.Invoke(repositoryId, response.Error ?? "Refresh projects failed");
            return false;
        }

        var projectsDetail = response.Data != null ? GetProjectsDetail(response.Data) : null;
        if (projectsDetail is { Count: > 0 })
            await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repositoryId, projectsDetail, cancellationToken);

        var link = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);
        if (link != null)
        {
            link.RepositoryType = ComputeRepositoryType(projectsDetail);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, [(repositoryId, projectsDetail)], persistDependencyLevel: true, cancellationToken);
        _logger.LogDebug("RefreshSingleRepositoryProjects completed for workspace {WorkspaceName}, repo {RepositoryId}", workspace.Name, repositoryId);
        return true;
    }

    /// <summary>Runs update for a single repository only: refresh that repo's projects, sync its dependencies, recompute and broadcast. Same behavior as Update but scoped to one repo (no commits). Stops on first error.</summary>
    public async Task RunUpdateSingleRepositoryAsync(
        int workspaceId,
        int repositoryId,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var pinnedLink = workspace.Repositories.FirstOrDefault(l => l.RepositoryId == repositoryId && !string.IsNullOrWhiteSpace(l.CheckedOutTag));
        if (pinnedLink != null)
            return;

        onProgressMessage?.Invoke("Refreshing repository projects...");
        var refreshOk = await RefreshSingleRepositoryProjectsAsync(workspaceId, repositoryId, onRepoError: onRepoError, cancellationToken: cancellationToken);
        if (!refreshOk)
            return;

        onProgressMessage?.Invoke("Syncing dependencies...");
        var syncedIds = await SyncDependenciesAsync(workspaceId, repoIdsToSync: new HashSet<int> { repositoryId }, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Synced dependencies {c} of {t}"), onRepoError: onRepoError, cancellationToken: cancellationToken);
        await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
        _logger.LogDebug("RunUpdateSingleRepository completed for workspace {WorkspaceName}, repo {RepositoryId}, synced={Count}", workspace.Name, repositoryId, syncedIds.Count);
    }

    /// <summary>Gets the list of repos that need dependency updates, with levels. Used to detect single vs multi-level and to drive update-with-commit flow. When <paramref name="repositoryIds"/> is set, only those repos are considered.</summary>
    public async Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(int workspaceId, IReadOnlySet<int>? repositoryIds = null, CancellationToken cancellationToken = default)
    {
        var payloads = await _workspaceProjectRepository.GetSyncDependenciesPayloadAsync(workspaceId, cancellationToken);
        var tagPinnedIds = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var withUpdates = payloads
            .Where(p => p.ProjectUpdates.Count > 0 && !tagPinnedIds.Contains(p.RepoId))
            .ToList();
        if (repositoryIds != null && repositoryIds.Count > 0)
            withUpdates = withUpdates.Where(p => repositoryIds.Contains(p.RepoId)).ToList();
        if (withUpdates.Count == 0)
            return (withUpdates, false);

        var levelsWithUpdates = withUpdates.Select(p => p.DependencyLevel ?? 0).Distinct().ToList();
        var isMultiLevel = levelsWithUpdates.Count > 1;
        return (withUpdates, isMultiLevel);
    }

    /// <summary>Syncs dependency versions in .csproj files to match the current version of each referenced package source. Only repos with at least one mismatched dependency are updated. When <paramref name="repoIdsToSync"/> is set, only those repos are synced. Returns the set of repo IDs where the agent reported UpdatedCount &gt; 0.</summary>
    public async Task<IReadOnlySet<int>> SyncDependenciesAsync(
        int workspaceId,
        Action<int, int, int>? onProgress = null,
        Action<int, string>? onRepoError = null,
        IReadOnlySet<int>? repoIdsToSync = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to sync dependencies.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var payloads = await _workspaceProjectRepository.GetSyncDependenciesPayloadAsync(workspaceId, cancellationToken);
        var tagPinnedIds = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var toSync = payloads
            .Where(p => p.ProjectUpdates.Count > 0 && (repoIdsToSync == null || repoIdsToSync.Contains(p.RepoId)))
            .Where(p => !tagPinnedIds.Contains(p.RepoId))
            .ToList();

        if (toSync.Count == 0)
        {
            _logger.LogInformation("Sync dependencies: no mismatched dependencies for workspace {WorkspaceName} (filtered)", workspace.Name);
            return new HashSet<int>();
        }

        _logger.LogInformation("Sync dependencies: Workspace={WorkspaceName}, RepoCount={RepoCount}", workspace.Name, toSync.Count);

        var completedCount = 0;
        var totalCount = toSync.Count;
        var failedRepoIds = new ConcurrentDictionary<int, bool>();
        var syncedRepoIds = new ConcurrentDictionary<int, bool>();
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        var repoTasks = toSync.Select(async repo =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectUpdates = repo.ProjectUpdates
                .Select(p => new
                {
                    projectPath = p.ProjectPath,
                    packageUpdates = p.PackageUpdates.Select(u => new { packageId = u.PackageId, newVersion = u.NewVersion }).ToList()
                })
                .ToList();

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepoName,
                projectUpdates,
                workspaceRoot
            };

            var response = await _agentBridge.SendCommandAsync("SyncRepositoryDependencies", args, cancellationToken);
            if (!response.Success)
            {
                failedRepoIds.TryAdd(repo.RepoId, true);
                onRepoError?.Invoke(repo.RepoId, response.Error ?? "Sync dependencies failed");
            }
            else
            {
                var syncResponse = response.Data != null
                    ? AgentResponseJson.DeserializeAgentResponse<SyncRepositoryDependenciesResponse>(response.Data)
                    : null;
                if (syncResponse?.UpdatedCount > 0)
                    syncedRepoIds.TryAdd(repo.RepoId, true);
            }

            var c = Interlocked.Increment(ref completedCount);
            onProgress?.Invoke(c, totalCount, repo.RepoId);
        });

        await Task.WhenAll(repoTasks);

        var updatesToPersist = toSync
            .Where(r => !failedRepoIds.ContainsKey(r.RepoId))
            .SelectMany(r => r.ProjectUpdates.SelectMany(p => p.PackageUpdates.Select(u => (r.RepoId, p.ProjectPath, u.PackageId, u.NewVersion))))
            .ToList();
        if (updatesToPersist.Count > 0)
            await _workspaceProjectRepository.UpdateProjectDependencyVersionsAsync(workspaceId, updatesToPersist, cancellationToken);

        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        _logger.LogDebug("Sync dependencies completed for workspace {WorkspaceName}. Synced {SyncedCount} repos (with changes), persisted {UpdateCount} versions", workspace.Name, syncedRepoIds.Count, updatesToPersist.Count);
        return syncedRepoIds.Keys.ToHashSet();
    }
}
