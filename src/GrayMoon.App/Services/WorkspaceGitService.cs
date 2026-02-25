using System.Collections.Concurrent;
using System.Text.Json;
using GrayMoon.App.Data;
using Microsoft.AspNetCore.SignalR;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

public class WorkspaceGitService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repositoryRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceGitService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null,
    PackageRegistrySyncService? packageRegistrySyncService = null,
    NuGetService? nuGetService = null,
    ConnectorRepository? connectorRepository = null)
{
    private readonly IAgentBridge _agentBridge = agentBridge ?? throw new ArgumentNullException(nameof(agentBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly GitHubRepositoryRepository _repositoryRepository = repositoryRepository ?? throw new ArgumentNullException(nameof(repositoryRepository));
    private readonly WorkspaceProjectRepository _workspaceProjectRepository = workspaceProjectRepository ?? throw new ArgumentNullException(nameof(workspaceProjectRepository));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspaceGitService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxConcurrentGitOperations ?? 8);
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext = hubContext;
    private readonly PackageRegistrySyncService? _packageRegistrySyncService = packageRegistrySyncService;
    private readonly NuGetService? _nuGetService = nuGetService;
    private readonly ConnectorRepository? _connectorRepository = connectorRepository;

    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        Action<int, int, int, RepoGitVersionInfo>? onProgress = null,
        IReadOnlyList<int>? repositoryIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to sync repositories.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, workspaceRoot, cancellationToken);

        var repos = workspace.Repositories
            .Select(link => link.Repository)
            .Where(r => r != null)
            .Cast<Repository>()
            .ToList();

        if (repositoryIds != null && repositoryIds.Count > 0)
            repos = repos.Where(r => repositoryIds.Contains(r.RepositoryId)).ToList();

        if (repos.Count == 0)
            return new Dictionary<int, RepoGitVersionInfo>();

        _logger.LogInformation("Sync triggered by user (workspace UI). Workspace={WorkspaceName}, RepoCount={RepoCount}", workspace.Name, repos.Count);

        var completedCount = 0;
        var totalCount = repos.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);

        var syncTasks = repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    cloneUrl = repo.CloneUrl,
                    bearerToken = repo.Connector?.UserToken,
                    workspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("SyncRepository", args, cancellationToken);
                var info = ParseSyncRepositoryResponse(response);
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount, repo.RepositoryId, info);
                return (repo.RepositoryId, info);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(syncTasks);

        await PersistVersionsAsync(workspaceId, results, cancellationToken);

        bool isInSync;
        if (repositoryIds != null && repositoryIds.Count > 0)
        {
            var allLinks = await _dbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId)
                .Select(wr => wr.SyncStatus)
                .ToListAsync(cancellationToken);
            isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);
        }
        else
        {
            isInSync = results.All(r => r.info.Version != "-" && r.info.Branch != "-");
        }
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        _logger.LogDebug("Sync completed for workspace {WorkspaceName}", workspace.Name);
        return results.ToDictionary(r => r.RepositoryId, r => r.info);
    }

    /// <summary>Refreshes project and package reference data from .csproj files on disk (no git). Merges into WorkspaceProjects and ProjectDependencies.</summary>
    public async Task RefreshWorkspaceProjectsAsync(
        int workspaceId,
        Action<int, int, int>? onProgress = null,
        Action<int, string>? onRepoError = null,
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
                var args = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot };
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

        foreach (var r in syncResults)
        {
            if (r.ProjectsDetail is { Count: > 0 })
                await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, r.RepositoryId, r.ProjectsDetail, cancellationToken);
        }

        var resultsForDeps = syncResults.Select(r => (r.RepositoryId, r.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, resultsForDeps, cancellationToken);

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

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var args = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot };
        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryProjects", args, cancellationToken);
        if (!response.Success)
        {
            onRepoError?.Invoke(repositoryId, response.Error ?? "Refresh projects failed");
            return false;
        }

        var projectsDetail = response.Data != null ? GetProjectsDetail(response.Data) : null;
        if (projectsDetail is { Count: > 0 })
            await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repositoryId, projectsDetail, cancellationToken);

        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, [(repositoryId, projectsDetail)], cancellationToken);
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

        onProgressMessage?.Invoke("Refreshing repository projects...");
        var refreshOk = await RefreshSingleRepositoryProjectsAsync(workspaceId, repositoryId, onRepoError: onRepoError, cancellationToken: cancellationToken);
        if (!refreshOk)
            return;

        onProgressMessage?.Invoke("Syncing dependencies...");
        var count = await SyncDependenciesAsync(workspaceId, repoIdsToSync: new HashSet<int> { repositoryId }, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Synced dependencies {c} of {t}"), onRepoError: onRepoError, cancellationToken: cancellationToken);
        await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
        _logger.LogDebug("RunUpdateSingleRepository completed for workspace {WorkspaceName}, repo {RepositoryId}, synced={Count}", workspace.Name, repositoryId, count);
    }

    /// <summary>Gets the list of repos that need dependency updates, with levels. Used to detect single vs multi-level and to drive update-with-commit flow.</summary>
    public async Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var payloads = await _workspaceProjectRepository.GetSyncDependenciesPayloadAsync(workspaceId, cancellationToken);
        var withUpdates = payloads.Where(p => p.ProjectUpdates.Count > 0).ToList();
        if (withUpdates.Count == 0)
            return (withUpdates, false);

        var levelsWithUpdates = withUpdates.Select(p => p.DependencyLevel ?? 0).Distinct().ToList();
        var isMultiLevel = levelsWithUpdates.Count > 1;
        return (withUpdates, isMultiLevel);
    }

    /// <summary>Gets push plan: all workspace repos by dependency level. Used to show multi-level dialog and run dependency-synchronized push.</summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool IsMultiLevel)> GetPushPlanAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var payload = await _workspaceProjectRepository.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
        if (payload.Count == 0)
            return (payload, false);
        var levels = payload.Select(p => p.DependencyLevel ?? 0).Distinct().ToList();
        var isMultiLevel = levels.Count > 1;
        return (payload, isMultiLevel);
    }

    /// <summary>Syncs dependency versions in .csproj files to match the current version of each referenced package source. Only repos with at least one mismatched dependency are updated. When <paramref name="repoIdsToSync"/> is set, only those repos are synced.</summary>
    public async Task<int> SyncDependenciesAsync(
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
        var toSync = payloads
            .Where(p => p.ProjectUpdates.Count > 0 && (repoIdsToSync == null || repoIdsToSync.Contains(p.RepoId)))
            .ToList();
        if (toSync.Count == 0)
        {
            _logger.LogInformation("Sync dependencies: no mismatched dependencies for workspace {WorkspaceName} (filtered)", workspace.Name);
            return 0;
        }

        _logger.LogInformation("Sync dependencies: Workspace={WorkspaceName}, RepoCount={RepoCount}", workspace.Name, toSync.Count);

        var completedCount = 0;
        var totalCount = toSync.Count;
        var failedRepoIds = new ConcurrentDictionary<int, bool>();
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

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        _logger.LogDebug("Sync dependencies completed for workspace {WorkspaceName}. Updated {RepoCount} repos, persisted {UpdateCount} versions", workspace.Name, toSync.Count, updatesToPersist.Count);
        return toSync.Count;
    }

    /// <summary>Broadcasts WorkspaceSynced so the grid refreshes. Call after SyncDependenciesAsync (which already recomputes and persists UnmatchedDeps).</summary>
    public async Task RecomputeAndBroadcastWorkspaceSyncedAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    /// <summary>Stages updated .csproj paths and commits with message "Update dependencies" plus one line per package (name version).</summary>
    public async Task<IReadOnlyList<(int RepoId, string? ErrorMessage)>> CommitDependencyUpdatesAsync(
        int workspaceId,
        IReadOnlyList<SyncDependenciesRepoPayload> reposToCommit,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected || reposToCommit.Count == 0)
            return Array.Empty<(int, string?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return reposToCommit.Select(r => (r.RepoId, (string?)"Workspace not found.")).ToList();

        var results = new List<(int RepoId, string? ErrorMessage)>();
        var completed = 0;
        var total = reposToCommit.Count;
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        foreach (var repo in reposToCommit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pathsToStage = repo.ProjectUpdates.Select(p => p.ProjectPath).Distinct().ToList();
            var lines = new List<string> { "chore(deps): update package versions", "" };
            foreach (var pu in repo.ProjectUpdates)
            {
                foreach (var (packageId, newVersion) in pu.PackageUpdates)
                    lines.Add($"- {packageId} to {newVersion}");
            }
            var commitMessage = string.Join("\r\n", lines);

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepoName,
                commitMessage,
                pathsToStage,
                workspaceRoot
            };
            var response = await _agentBridge.SendCommandAsync("StageAndCommit", args, cancellationToken);
            var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data) is { Success: true };
            var err = success ? null : (response.Error ?? AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data!)?.ErrorMessage ?? "Commit failed");
            results.Add((repo.RepoId, err));
            completed++;
            onProgress?.Invoke(completed, total, repo.RepoId);
        }

        return results;
    }

    /// <summary>Runs full update (refresh projects, sync deps, optional commits). Stops on first error and reports it via onRepoError (message under the repo). Single-level: one pass then commit all. Multi-level: per level sync+commit then refresh version for committed repos, repeat.</summary>
    public async Task RunUpdateAsync(
        int workspaceId,
        bool withCommits,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var hadError = false;
        void OnRepoError(int repoId, string msg)
        {
            hadError = true;
            onRepoError?.Invoke(repoId, msg);
        }

        onProgressMessage?.Invoke("Refreshing projects...");
        await RefreshWorkspaceProjectsAsync(
            workspaceId,
            onProgress: (c, t, _) => onProgressMessage?.Invoke($"Refreshing projects {c} of {t}"),
            onRepoError: OnRepoError,
            cancellationToken);
        if (hadError)
            return;

        var (payload, isMultiLevel) = await GetUpdatePlanAsync(workspaceId, cancellationToken);
        if (payload.Count == 0)
        {
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            return;
        }

        if (!withCommits)
        {
            onProgressMessage?.Invoke("Syncing dependencies...");
            await SyncDependenciesAsync(workspaceId, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Synced dependencies {c} of {t}"), onRepoError: OnRepoError, cancellationToken: cancellationToken);
            if (hadError)
                return;
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            return;
        }

        if (isMultiLevel)
        {
            var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
            foreach (var level in levelsAsc)
            {
                if (hadError)
                    break;

                var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
                if (reposAtLevel.Count == 0) continue;

                var repoIds = reposAtLevel.Select(r => r.RepoId).ToHashSet();
                onProgressMessage?.Invoke($"Updating {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")} for dependency level {level}...");
                await SyncDependenciesAsync(workspaceId, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Syncing {c} of {t} for dependency level {level}"), onRepoError: OnRepoError, repoIdsToSync: repoIds, cancellationToken: cancellationToken);
                if (hadError)
                    break;

                onProgressMessage?.Invoke($"Committing for dependency level {level}...");
                var commitResults = await CommitDependencyUpdatesAsync(workspaceId, reposAtLevel, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Committing {c} of {t} for dependency level {level}"), cancellationToken: cancellationToken);
                foreach (var (repoId, errMsg) in commitResults)
                {
                    if (!string.IsNullOrEmpty(errMsg))
                    {
                        OnRepoError(repoId, errMsg);
                        break;
                    }
                }
                if (hadError)
                    break;

                foreach (var repo in reposAtLevel)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (hadError)
                        break;
                    onProgressMessage?.Invoke($"Refreshing version for {repo.RepoName}...");
                    var (refreshSuccess, refreshError) = await SyncSingleRepositoryAsync(repo.RepoId, workspaceId, cancellationToken);
                    if (!refreshSuccess)
                    {
                        OnRepoError(repo.RepoId, refreshError ?? "Refresh version failed.");
                        break;
                    }
                }
            }
        }
        else
        {
            onProgressMessage?.Invoke("Syncing dependencies...");
            await SyncDependenciesAsync(workspaceId, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Synced dependencies {c} of {t}"), onRepoError: OnRepoError, cancellationToken: cancellationToken);
            if (hadError)
                return;
            onProgressMessage?.Invoke("Committing updates...");
            var commitResults = await CommitDependencyUpdatesAsync(workspaceId, payload, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Committing {c} of {t}"), cancellationToken: cancellationToken);
            foreach (var (repoId, errMsg) in commitResults)
            {
                if (!string.IsNullOrEmpty(errMsg))
                {
                    OnRepoError(repoId, errMsg);
                    break;
                }
            }
        }

        if (!hadError)
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
    }

    /// <summary>Runs dependency-synchronized push: sync package registries, then push by level (lowest first). For each level, waits until required packages are in registry (or pushes all at once if not possible), then pushes all repos at that level in parallel. Ensures branch is upstreamed even when there are no commits to push. When <paramref name="repoIdsToPush"/> is set, only those repos are pushed.</summary>
    public async Task RunPushAsync(
        int workspaceId,
        IReadOnlySet<int>? repoIdsToPush = null,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, workspaceRoot, cancellationToken);

        onProgressMessage?.Invoke("Syncing package registries...");
        if (_packageRegistrySyncService != null)
            await _packageRegistrySyncService.SyncWorkspacePackageRegistriesAsync(workspaceId, cancellationToken: cancellationToken);

        var fullPayload = await _workspaceProjectRepository.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
        var payload = repoIdsToPush is { Count: > 0 }
            ? fullPayload.Where(p => repoIdsToPush.Contains(p.RepoId)).ToList()
            : fullPayload;
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var bearerByRepoId = links.Where(wr => wr.Repository != null).ToDictionary(wr => wr.RepositoryId, wr => wr.Repository!.Connector?.UserToken);

        bool synchronizedPushPossible = payload.All(p => p.RequiredPackages.All(r => r.MatchedConnectorId.HasValue));
        if (!synchronizedPushPossible && payload.Any(p => p.RequiredPackages.Count > 0))
        {
            _logger.LogInformation("Push: some dependencies have no matched registry; pushing all repositories at once.");
        }

        if (!synchronizedPushPossible || _nuGetService == null || _connectorRepository == null)
        {
            onProgressMessage?.Invoke("Pushing all repositories...");
            await PushReposAsync(workspace, payload, bearerByRepoId, onProgressMessage, onRepoError, cancellationToken);
            await RefreshVersionsAfterPushAsync(workspaceId, payload, cancellationToken);
            if (_hubContext != null)
                await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            return;
        }

        var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
        foreach (var level in levelsAsc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
            if (reposAtLevel.Count == 0) continue;

            var requiredForLevel = reposAtLevel
                .SelectMany(r => r.RequiredPackages)
                .DistinctBy(r => (r.PackageId, r.Version, r.MatchedConnectorId))
                .Where(r => r.MatchedConnectorId.HasValue)
                .ToList();
            var totalDeps = requiredForLevel.Count;

            if (totalDeps > 0)
            {
                var timeoutMinutes = totalDeps * Math.Max(0.1, workspaceOptions.Value.PushWaitDependencyTimeoutMinutesPerDependency);
                var totalTimeout = TimeSpan.FromMinutes(timeoutMinutes);
                using var timeoutCts = new CancellationTokenSource(totalTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var linkedToken = linkedCts.Token;
                var deadline = DateTime.UtcNow + totalTimeout;
                var found = 0;
                var lastPollUtc = DateTime.MinValue;
                while (found < totalDeps)
                {
                    linkedToken.ThrowIfCancellationRequested();
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        onProgressMessage?.Invoke("Timed out.");
                        throw new OperationCanceledException("Push wait for dependencies timed out.");
                    }
                    var line1 = found == 0
                        ? $"Waiting for {totalDeps} {(totalDeps == 1 ? "dependency" : "dependencies")}..."
                        : $"{found} of {totalDeps} dependencies...";
                    var totalSec = (int)remaining.TotalSeconds;
                    var mm = totalSec / 60;
                    var ss = totalSec % 60;
                    onProgressMessage?.Invoke($"{line1}\n{mm:D2}:{ss:D2}");

                    if ((DateTime.UtcNow - lastPollUtc).TotalSeconds >= 2)
                    {
                        found = 0;
                        foreach (var req in requiredForLevel)
                        {
                            var connector = await _connectorRepository.GetByIdAsync(req.MatchedConnectorId!.Value);
                            if (connector != null && await _nuGetService.PackageVersionExistsAsync(connector, req.PackageId, req.Version, linkedToken))
                                found++;
                        }
                        lastPollUtc = DateTime.UtcNow;
                    }

                    if (found >= totalDeps)
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(1), linkedToken);
                }
            }

            onProgressMessage?.Invoke($"Pushing {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")} for dependency level {level}...");
            await PushReposAsync(workspace, reposAtLevel, bearerByRepoId, onProgressMessage, onRepoError, cancellationToken);
            await RefreshVersionsAfterPushAsync(workspaceId, reposAtLevel, cancellationToken);
        }

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    private async Task PushReposAsync(
        Workspace workspace,
        IReadOnlyList<PushRepoPayload> repos,
        IReadOnlyDictionary<int, string?> bearerByRepoId,
        Action<string>? onProgressMessage,
        Action<int, string>? onRepoError,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        var total = repos.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var pushTasks = repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepoId,
                    repositoryName = repo.RepoName,
                    bearerToken = bearerByRepoId.GetValueOrDefault(repo.RepoId),
                    workspaceId = workspace.WorkspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("PushRepository", args, cancellationToken);
                var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data) is { Success: true };
                if (!success)
                {
                    var err = response.Error ?? AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data!)?.ErrorMessage ?? "Push failed";
                    onRepoError?.Invoke(repo.RepoId, err);
                }
                var c = Interlocked.Increment(ref completed);
                onProgressMessage?.Invoke($"Pushed {c} of {total}");
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(pushTasks);
    }

    private async Task RefreshVersionsAfterPushAsync(int workspaceId, IReadOnlyList<PushRepoPayload> repos, CancellationToken cancellationToken)
    {
        if (repos.Count == 0) return;
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncSingleRepositoryAsync(repo.RepoId, workspaceId, cancellationToken);
        }
    }

    /// <summary>Refreshes version for a single repo and persists. Returns (success, errorMessage) for caller to report and optionally stop workflow.</summary>
    public async Task<(bool Success, string? ErrorMessage)> SyncSingleRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
        {
            _logger.LogWarning("Sync skipped: repository not found for id {RepositoryId}", repositoryId);
            return (false, "Repository not found.");
        }

        var isInWorkspace = await _dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repo.RepositoryId, cancellationToken);
        if (!isInWorkspace)
        {
            _logger.LogWarning("Sync skipped: repository {RepositoryName} (id {RepositoryId}) is not linked to workspace {WorkspaceId}", repo.RepositoryName, repositoryId, workspaceId);
            return (false, "Repository is not linked to this workspace.");
        }

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, "Workspace not found.");

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryVersion", new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot }, cancellationToken);
        if (!response.Success)
        {
            var err = response.Error ?? "Refresh version failed.";
            _logger.LogWarning("RefreshRepositoryVersion failed for repo {RepositoryId}: {Error}", repositoryId, err);
            return (false, err);
        }

        var info = ParseRefreshRepositoryVersionResponse(response);

        await PersistVersionsAsync(workspaceId, [(repo.RepositoryId, info)], cancellationToken);

        var allLinks = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.SyncStatus)
            .ToListAsync(cancellationToken);
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        return (true, null);
    }

    public async Task<IReadOnlyDictionary<int, RepoSyncStatus>> GetRepoSyncStatusAsync(
        int workspaceId,
        Action<int, RepoSyncStatus>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, RepoSyncStatus>();
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return result;

        var workspaceRepos = workspace.Repositories.ToList();
        if (workspaceRepos.Count == 0)
            return result;

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        foreach (var wr in workspaceRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repo = wr.Repository;
            if (repo == null) continue;

            var response = await _agentBridge.SendCommandAsync("GetRepositoryVersion", new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot }, cancellationToken);
            RepoSyncStatus status;
            if (!response.Success || response.Data == null)
                status = RepoSyncStatus.Error;
            else
                status = ParseGetRepositoryVersionToStatus(response.Data, wr.GitVersion, wr.BranchName);

            result[repo.RepositoryId] = status;
            onProgress?.Invoke(repo.RepositoryId, status);
        }

        var isInSync = result.Values.All(v => v == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateIsInSyncAsync(workspaceId, isInSync);
        return result;
    }

    private static RepoGitVersionInfo ParseSyncRepositoryResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-", ErrorMessage = response.Error ?? "Sync failed" };

        var (version, branch, gitVersionError) = GetVersionBranch(response.Data);
        var projectsCount = GetProjects(response.Data);
        var projectsDetail = GetProjectsDetail(response.Data);
        var (outgoingCommits, incomingCommits) = GetCommitCounts(response.Data);
        var (localBranches, remoteBranches, defaultBranch) = GetBranches(response.Data);
        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Projects = projectsCount,
            ProjectsDetail = projectsDetail,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            DefaultBranch = defaultBranch,
            ErrorMessage = gitVersionError
        };
    }

    private static RepoGitVersionInfo ParseRefreshRepositoryVersionResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch, gitVersionError) = GetVersionBranch(response.Data);
        var (outgoingCommits, incomingCommits) = GetCommitCounts(response.Data);
        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            ErrorMessage = gitVersionError
        };
    }

    private static (string version, string branch, string? gitVersionError) GetVersionBranch(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        return (r?.Version ?? "-", r?.Branch ?? "-", r?.GitVersionError);
    }

    private static int? GetProjects(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        var projects = r?.Projects;
        if (projects == null) return null;
        return projects.Count > 0 ? projects.Count : null;
    }

    private static (int? Outgoing, int? Incoming) GetCommitCounts(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(data);
        return (r?.OutgoingCommits, r?.IncomingCommits);
    }

    private static (IReadOnlyList<string>? LocalBranches, IReadOnlyList<string>? RemoteBranches, string? DefaultBranch) GetBranches(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentBranchesResponse>(data);
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var defaultBranch = !string.IsNullOrWhiteSpace(r?.DefaultBranch) ? r.DefaultBranch : null;
        return (local, remote, defaultBranch);
    }

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        var projects = r?.Projects;
        if (projects == null || projects.Count == 0) return null;
        var list = new List<SyncProjectInfo>();
        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var projectType = p.ProjectType >= 0 && p.ProjectType <= 4 ? (ProjectType)p.ProjectType : ProjectType.Library;
            var packageRefs = (p.PackageReferences ?? new List<AgentPackageRefDto>())
                .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                .Select(pr => new SyncPackageReference(pr.Name!.Trim(), pr.Version ?? ""))
                .ToList();
            list.Add(new SyncProjectInfo(
                p.Name,
                projectType,
                p.ProjectPath ?? "",
                p.TargetFramework ?? "",
                p.PackageId,
                packageRefs));
        }
        return list.Count > 0 ? list : null;
    }

    private static RepoSyncStatus ParseGetRepositoryVersionToStatus(object data, string? persistedVersion, string? persistedBranch)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentGetRepositoryVersionResponse>(data);
        if (r == null || !r.Exists)
            return RepoSyncStatus.NotCloned;
        if (string.IsNullOrEmpty(r.Version) || string.IsNullOrEmpty(r.Branch))
            return RepoSyncStatus.VersionMismatch;
        return (r.Version == persistedVersion && r.Branch == persistedBranch) ? RepoSyncStatus.InSync : RepoSyncStatus.VersionMismatch;
    }

    private async Task PersistVersionsAsync(
        int workspaceId,
        IEnumerable<(int RepoId, RepoGitVersionInfo info)> results,
        CancellationToken cancellationToken)
    {
        var resultList = results.ToList();
        if (resultList.Count == 0) return;

        var repoIds = resultList.Select(r => r.RepoId).ToList();
        var workspaceReposToUpdate = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var (repoId, info) in resultList)
        {
            var wr = workspaceReposToUpdate.FirstOrDefault(w => w.RepositoryId == repoId);
            if (wr != null)
            {
                wr.GitVersion = info.Version == "-" ? null : info.Version;
                wr.BranchName = info.Branch == "-" ? null : info.Branch;
                if (info.Projects.HasValue) wr.Projects = info.Projects;
                if (info.OutgoingCommits.HasValue) wr.OutgoingCommits = info.OutgoingCommits;
                if (info.IncomingCommits.HasValue) wr.IncomingCommits = info.IncomingCommits;
                wr.SyncStatus = (info.Version == "-" || info.Branch == "-") ? RepoSyncStatus.Error : RepoSyncStatus.InSync;
            }

            if (info.ProjectsDetail is { Count: > 0 })
                await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repoId, info.ProjectsDetail, cancellationToken);

            // Persist branches if available (include default branch so IsDefault is set)
            if ((info.LocalBranches != null || info.RemoteBranches != null) && wr != null)
            {
                await PersistBranchesAsync(wr.WorkspaceRepositoryId, info.LocalBranches, info.RemoteBranches, info.DefaultBranch, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }

    /// <summary>Persists branches for a workspace repository. Removes branches not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master).</summary>
    public async Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existingBranches = await _dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId)
            .ToListAsync(cancellationToken);

        var fetchedBranches = new HashSet<(string Name, bool IsRemote)>();
        if (localBranches != null)
        {
            foreach (var branch in localBranches)
            {
                fetchedBranches.Add((branch, false));
            }
        }
        if (remoteBranches != null)
        {
            foreach (var branch in remoteBranches)
            {
                fetchedBranches.Add((branch, true));
            }
        }

        // Clear IsDefault for all existing; we will set it for the default branch below
        foreach (var b in existingBranches)
            b.IsDefault = false;

        // Update existing branches or add new ones
        foreach (var (name, isRemote) in fetchedBranches)
        {
            var isDefault = !string.IsNullOrWhiteSpace(defaultBranchName) && string.Equals(name, defaultBranchName, StringComparison.OrdinalIgnoreCase);
            var existing = existingBranches.FirstOrDefault(b => b.BranchName == name && b.IsRemote == isRemote);
            if (existing != null)
            {
                existing.LastSeenAt = now;
                existing.IsDefault = isDefault;
            }
            else
            {
                _dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = workspaceRepositoryId,
                    BranchName = name,
                    IsRemote = isRemote,
                    LastSeenAt = now,
                    IsDefault = isDefault
                });
            }
        }

        // Remove branches that were not fetched (no longer exist)
        var toRemove = existingBranches.Where(b => !fetchedBranches.Contains((b.BranchName, b.IsRemote))).ToList();
        if (toRemove.Count > 0)
        {
            _dbContext.RepositoryBranches.RemoveRange(toRemove);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Creates a new branch in all workspace repos (in parallel), then checks it out. baseBranch is "__default__" to use each repo's default, or a branch name. Calls onProgress(completed, total).</summary>
    public async Task CreateBranchesAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to create branches.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var links = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Include(wr => wr.Repository)
            .ToListAsync(cancellationToken);

        if (links.Count == 0)
            return;

        var useDefaultBase = string.Equals(baseBranch, "__default__", StringComparison.OrdinalIgnoreCase);
        var completedCount = 0;
        var totalCount = links.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        async Task ProcessOne(WorkspaceRepositoryLink wr)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var repo = wr.Repository;
                if (repo == null)
                    return;

                string baseBranchName;
                if (useDefaultBase)
                {
                    var defaultRow = await _dbContext.RepositoryBranches
                        .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault)
                        .Select(rb => rb.BranchName)
                        .FirstOrDefaultAsync(cancellationToken);
                    baseBranchName = defaultRow ?? "main";
                }
                else
                {
                    baseBranchName = baseBranch;
                }

                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryName = repo.RepositoryName,
                    newBranchName,
                    baseBranchName,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("CreateBranch", args, cancellationToken);
                var createResponse = AgentResponseJson.DeserializeAgentResponse<CreateBranchResponse>(response.Data);
                var success = createResponse?.Success ?? response.Success;

                if (success)
                    wr.BranchName = newBranchName;
            }
            finally
            {
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount);
                semaphore.Release();
            }
        }

        await Task.WhenAll(links.Select(ProcessOne));
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Persist the new branch for each repo where creation succeeded (so it appears in branch lists without a manual refresh)
        foreach (var wr in links.Where(wr => wr.BranchName == newBranchName))
        {
            await EnsureLocalBranchPersistedAsync(wr.WorkspaceRepositoryId, newBranchName, cancellationToken);
        }

        _hubContext?.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
    }

    /// <summary>Ensures a local branch is present in RepositoryBranches for the given workspace repository. Adds it if missing; does not remove other branches.</summary>
    private async Task EnsureLocalBranchPersistedAsync(int workspaceRepositoryId, string branchName, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.RepositoryBranches
            .AnyAsync(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId && rb.BranchName == branchName && !rb.IsRemote, cancellationToken);
        if (exists)
            return;
        _dbContext.RepositoryBranches.Add(new RepositoryBranch
        {
            WorkspaceRepositoryId = workspaceRepositoryId,
            BranchName = branchName,
            IsRemote = false,
            LastSeenAt = DateTime.UtcNow,
            IsDefault = false
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
