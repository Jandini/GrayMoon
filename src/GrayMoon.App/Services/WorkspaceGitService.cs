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

namespace GrayMoon.App.Services;

public class WorkspaceGitService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repositoryRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    WorkspaceDependencyService workspaceDependencyService,
    WorkspacePullRequestService workspacePullRequestService,
    RepositoryBranchWriter branchWriter,
    WorkspaceRepositoryStateWriter stateWriter,
    WorkspaceStateRecomputeScope recomputeScope,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceGitService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null,
    PackageRegistrySyncService? packageRegistrySyncService = null,
    NuGetService? nuGetService = null,
    ConnectorRepository? connectorRepository = null,
    ConnectorHealthService? connectorHealthService = null,
    WorkspaceFileVersionService? fileVersionService = null)
{
    private readonly IAgentBridge _agentBridge = agentBridge ?? throw new ArgumentNullException(nameof(agentBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly GitHubRepositoryRepository _repositoryRepository = repositoryRepository ?? throw new ArgumentNullException(nameof(repositoryRepository));
    private readonly WorkspaceProjectRepository _workspaceProjectRepository = workspaceProjectRepository ?? throw new ArgumentNullException(nameof(workspaceProjectRepository));
    private readonly WorkspaceDependencyService _workspaceDependencyService = workspaceDependencyService ?? throw new ArgumentNullException(nameof(workspaceDependencyService));
    private readonly WorkspacePullRequestService _workspacePullRequestService = workspacePullRequestService ?? throw new ArgumentNullException(nameof(workspacePullRequestService));
    private readonly RepositoryBranchWriter _branchWriter = branchWriter ?? throw new ArgumentNullException(nameof(branchWriter));
    private readonly WorkspaceRepositoryStateWriter _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    private readonly WorkspaceStateRecomputeScope _recomputeScope = recomputeScope ?? throw new ArgumentNullException(nameof(recomputeScope));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspaceGitService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 16);
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext = hubContext;
    private readonly PackageRegistrySyncService? _packageRegistrySyncService = packageRegistrySyncService;
    private readonly NuGetService? _nuGetService = nuGetService;
    private readonly ConnectorRepository? _connectorRepository = connectorRepository;
    private readonly ConnectorHealthService? _connectorHealthService = connectorHealthService;
    private readonly WorkspaceFileVersionService? _fileVersionService = fileVersionService;

    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        Action<int, int, int, RepoGitVersionInfo>? onProgress = null,
        Action? onAppSideComplete = null,
        IReadOnlyList<int>? repositoryIds = null,
        bool skipDependencyLevelPersistence = false,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

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

        // EF Core DbContext is not thread-safe; run health checks sequentially before the parallel block.
        if (_connectorHealthService != null)
        {
            foreach (var repo in repos)
                await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);
        }

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
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("SyncRepository", args, cancellationToken);
                var info = ParseSyncRepositoryResponse(response);
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount, repo.RepositoryId, info);
                if (count == totalCount)
                    onAppSideComplete?.Invoke();
                return (repo.RepositoryId, info);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(syncTasks);

        await PersistVersionsAsync(workspaceId, results, persistDependencyLevel: !skipDependencyLevelPersistence, cancellationToken);

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

        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);

        _logger.LogDebug("Sync completed for workspace {WorkspaceName}", workspace.Name);
        return results.ToDictionary(r => r.RepositoryId, r => r.info);
    }

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

        foreach (var r in syncResults)
        {
            if (r.ProjectsDetail is { Count: > 0 })
                await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, r.RepositoryId, r.ProjectsDetail, cancellationToken);
        }

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

    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache &lt;project.csproj&gt;</c> for each specified project file.
    /// Best-effort: errors are logged and swallowed so the caller's workflow is never interrupted.
    /// </summary>
    public async Task RestoreDependenciesAsync(int workspaceId, IEnumerable<(string RepoName, IReadOnlyList<string> ProjectPaths)> repos, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var tasks = repos
            .Where(r => r.ProjectPaths.Count > 0)
            .Select(async r =>
            {
                try
                {
                    await _agentBridge.SendCommandAsync(
                        "DotnetRestore",
                        new { workspaceName = workspace.Name, repositoryName = r.RepoName, projectPaths = r.ProjectPaths, workspaceRoot },
                        cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "dotnet restore failed for {RepoName} in workspace {WorkspaceName}, continuing", r.RepoName, workspace.Name);
                }
            });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache</c> for all tracked project files across all workspace
    /// repositories, skipping repos pinned to a tag. Best-effort: individual restore errors are logged and swallowed.
    /// Returns the total number of project files targeted for restore.
    /// </summary>
    public async Task<int> RestoreAllWorkspacePackagesAsync(
        int workspaceId,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        setProgress("Restoring packages...");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return 0;

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        await RestoreDependenciesAsync(workspaceId, repoGroups, cancellationToken);
        return totalCount;
    }

    public async Task<int> RestoreSyncedWorkspacePackagesAsync(
        int workspaceId,
        IReadOnlySet<int> syncedRepoIds,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        if (syncedRepoIds.Count == 0)
            return 0;

        setProgress("Restoring packages...");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return 0;

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && syncedRepoIds.Contains(p.RepositoryId)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        await RestoreDependenciesAsync(workspaceId, repoGroups, cancellationToken);
        return totalCount;
    }

    /// <summary>
    /// Closes out a user action: recomputes workspace-wide file-version and dependency stats, then
    /// broadcasts WorkspaceSynced once so the grid refreshes. Call exactly once per action, after every
    /// repository in the batch has been written.
    /// </summary>
    public Task RecomputeAndBroadcastWorkspaceSyncedAsync(int workspaceId, CancellationToken cancellationToken = default)
        => _recomputeScope.CompleteAsync(workspaceId, cancellationToken);

    /// <summary>Stages updated .csproj paths and commits with message "chore(deps): update package versions" plus the full list of packages (one line per package: "- {packageId} to {version}"). Runs up to 8 commits in parallel.</summary>
    public async Task<IReadOnlyList<(int RepoId, bool Committed, string? ErrorMessage)>> CommitDependencyUpdatesAsync(
        int workspaceId,
        IReadOnlyList<SyncDependenciesRepoPayload> reposToCommit,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? commitMessageOverride = null,
        bool includeDepsInCommitMessage = true)
    {
        if (!_agentBridge.IsAgentConnected || reposToCommit.Count == 0)
            return Array.Empty<(int, bool, string?)>();

        var tagPinnedIds = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();
        reposToCommit = reposToCommit.Where(r => !tagPinnedIds.Contains(r.RepoId)).ToList();
        if (reposToCommit.Count == 0)
            return Array.Empty<(int, bool, string?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return reposToCommit.Select(r => (r.RepoId, false, (string?)"Workspace not found.")).ToList();

        var total = reposToCommit.Count;
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var completed = 0;
        var semaphore = new SemaphoreSlim(_maxConcurrent);

        var tasks = reposToCommit.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Paths must be repo-relative with forward slashes for reliable git add across platforms.
                var pathsToStage = repo.ProjectUpdates
                    .Select(p => (p.ProjectPath ?? "").Trim().Replace('\\', '/'))
                    .Where(p => p.Length > 0)
                    .Distinct()
                    .ToList();
                var subject = string.IsNullOrWhiteSpace(commitMessageOverride)
                    ? "chore(deps): update package versions"
                    : commitMessageOverride.Trim();
                string commitMessage;
                if (includeDepsInCommitMessage)
                {
                    var lines = new List<string> { subject, "" };
                    var seen = new HashSet<(string Id, string Version)>();
                    foreach (var pu in repo.ProjectUpdates)
                    {
                        foreach (var (packageId, _, newVersion) in pu.PackageUpdates)
                        {
                            if (seen.Add((packageId, newVersion)))
                                lines.Add($"- {packageId} to {newVersion}");
                        }
                    }
                    commitMessage = string.Join("\r\n", lines);
                }
                else
                {
                    commitMessage = subject;
                }

                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryName = repo.RepoName,
                    commitMessage,
                    pathsToStage,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("StageAndCommit", args, cancellationToken);
                var parsed = response.Success && response.Data != null
                    ? AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data)
                    : null;
                var agentSuccess = parsed is { Success: true };
                var agentCommitted = parsed?.Committed ?? false;
                var err = agentSuccess ? null : (response.Error ?? parsed?.ErrorMessage ?? "Commit failed");
                var c = Interlocked.Increment(ref completed);
                onProgress?.Invoke(c, total, repo.RepoId);
                return (RepoId: repo.RepoId, Committed: agentCommitted, ErrorMessage: err);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        var byRepo = completedResults.ToDictionary(x => x.RepoId, x => (x.Committed, x.ErrorMessage));
        return reposToCommit.Select(r => (r.RepoId, byRepo[r.RepoId].Committed, byRepo[r.RepoId].ErrorMessage)).ToList();
    }

    /// <summary>Stages the given file paths per repo and commits with message "chore(deps): update versions (N)" where N is the path count for that repo. Uses the same agent StageAndCommit command.</summary>
    public async Task<IReadOnlyList<(int RepoId, bool Committed, string? ErrorMessage)>> CommitFilePathsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> reposAndPaths,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? commitMessageOverride = null)
    {
        if (!_agentBridge.IsAgentConnected || reposAndPaths.Count == 0)
            return Array.Empty<(int, bool, string?)>();

        var tagPinnedIdsFp = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();
        reposAndPaths = reposAndPaths.Where(r => !tagPinnedIdsFp.Contains(r.RepoId)).ToList();
        if (reposAndPaths.Count == 0)
            return Array.Empty<(int, bool, string?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return reposAndPaths.Select(r => (r.RepoId, false, (string?)"Workspace not found.")).ToList();

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var total = reposAndPaths.Count;
        var completed = 0;
        var semaphore = new SemaphoreSlim(_maxConcurrent);

        var tasks = reposAndPaths.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var pathsToStage = repo.FilePaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim().Replace('\\', '/'))
                    .Distinct()
                    .ToList();
                if (pathsToStage.Count == 0)
                    return (RepoId: repo.RepoId, Committed: false, ErrorMessage: (string?)"No paths to stage.");
                var commitMessage = string.IsNullOrWhiteSpace(commitMessageOverride)
                    ? $"chore(deps): update versions ({pathsToStage.Count})"
                    : commitMessageOverride.Trim();
                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryName = repo.RepoName,
                    commitMessage,
                    pathsToStage,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("StageAndCommit", args, cancellationToken);
                var parsed = response.Success && response.Data != null
                    ? AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data)
                    : null;
                var agentSuccess = parsed is { Success: true };
                var agentCommitted = parsed?.Committed ?? false;
                var err = agentSuccess ? null : (response.Error ?? parsed?.ErrorMessage ?? "Commit failed");
                var c = Interlocked.Increment(ref completed);
                onProgress?.Invoke(c, total, repo.RepoId);
                return (RepoId: repo.RepoId, Committed: agentCommitted, ErrorMessage: err);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        var byRepo = completedResults.ToDictionary(x => x.RepoId, x => (x.Committed, x.ErrorMessage));
        return reposAndPaths.Select(r => (r.RepoId, byRepo[r.RepoId].Committed, byRepo[r.RepoId].ErrorMessage)).ToList();
    }

    /// <summary>Runs GetCommitCounts (agent) for each repo and returns DefaultBranchAhead and HasUpstream per repo. Used to check if sync-to-default is safe (no commits ahead of default). Respects MaxParallelOperations.</summary>
    public async Task<IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>> GetCommitCountsForReposAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string RepoName)> repos,
        CancellationToken cancellationToken = default)
    {
        if (repos.Count == 0)
            return Array.Empty<(int, int?, bool?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Array.Empty<(int, int?, bool?)>();

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var maxParallel = _maxConcurrent;

        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = repos.Select(async tuple =>
        {
            var (repoId, repoName) = tuple;
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    var response = await _agentBridge.SendCommandAsync("GetCommitCounts", new
                    {
                        workspaceName = workspace.Name,
                        repositoryName = repoName,
                        workspaceRoot
                    }, cancellationToken);
                    if (!response.Success || response.Data == null)
                        return (RepoId: repoId, DefaultAhead: (int?)null, HasUpstream: (bool?)null);
                    var data = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(response.Data);
                    return (RepoId: repoId, DefaultAhead: data?.DefaultBranchAhead, HasUpstream: data?.HasUpstream);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetCommitCounts failed for repo {RepoId} ({RepoName})", repoId, repoName);
                    return (RepoId: repoId, DefaultAhead: (int?)null, HasUpstream: (bool?)null);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Runs git fetch + tag list + commit counts for every repo in the workspace (no GitVersion, no csproj scan,
    /// no branch listing). Updates only commit-count fields and RepositoryBranch tag rows in the DB.
    /// Significantly faster than a full Sync.
    /// </summary>
    public async Task QuickFetchAsync(
        int workspaceId,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        var links = workspace.Repositories
            .Where(l => l.Repository != null)
            .ToList();

        if (links.Count == 0) return;

        _logger.LogInformation("Quick Fetch triggered. Workspace={WorkspaceName}, RepoCount={Count}", workspace.Name, links.Count);

        var completedCount = 0;
        var totalCount = links.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);

        var fetchTasks = links.Select(async link =>
        {
            var repo = link.Repository!;
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("FetchCommits", args, cancellationToken);
                var data = response.Success && response.Data != null
                    ? AgentResponseJson.DeserializeAgentResponse<AgentFetchCommitsResponse>(response.Data)
                    : null;
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount);
                return (link.WorkspaceRepositoryId, repo.RepositoryId, data);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(fetchTasks);

        // Update commit-count fields on WorkspaceRepositoryLink rows.
        // EF Core not thread-safe; query sequentially after the parallel fetch.
        var repoIds = results.Select(r => r.RepositoryId).ToList();
        var wrLinks = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var (_, repoId, data) in results)
        {
            if (data == null) continue;
            var wr = wrLinks.FirstOrDefault(w => w.RepositoryId == repoId);
            if (wr == null) continue;

            if (data.OutgoingCommits.HasValue) wr.OutgoingCommits = data.OutgoingCommits;
            if (data.IncomingCommits.HasValue) wr.IncomingCommits = data.IncomingCommits;
            if (data.HasUpstream.HasValue) wr.BranchHasUpstream = data.HasUpstream.Value;
            if (data.DefaultBranchBehind.HasValue) wr.DefaultBranchBehindCommits = data.DefaultBranchBehind;
            if (data.DefaultBranchAhead.HasValue) wr.DefaultBranchAheadCommits = data.DefaultBranchAhead;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Update RepositoryBranch tag rows and HasNewerTag. Pass localBranches/remoteBranches as null so
        // existing branch rows are not touched - only tag rows are refreshed.
        foreach (var (wrId, _, data) in results)
        {
            if (data?.Tags == null && string.IsNullOrWhiteSpace(data?.CurrentTag)) continue;
            await PersistBranchesAsync(wrId, localBranches: null, remoteBranches: null, defaultBranchName: null,
                tags: data?.Tags, currentTag: data?.CurrentTag, cancellationToken);
        }

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

        _logger.LogDebug("Quick Fetch completed for workspace {WorkspaceName}", workspace.Name);
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
        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryVersion", new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, repositoryId = repo.RepositoryId, workspaceRoot }, cancellationToken);
        if (!response.Success)
        {
            var err = response.Error ?? "Refresh version failed.";
            _logger.LogWarning("RefreshRepositoryVersion failed for repo {RepositoryId}: {Error}", repositoryId, err);
            return (false, err);
        }

        var info = ParseRefreshRepositoryVersionResponse(response);

        await PersistVersionsAsync(workspaceId, [(repo.RepositoryId, info)], true, cancellationToken);

        var allLinks = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.SyncStatus)
            .ToListAsync(cancellationToken);
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        return (true, null);
    }

    /// <summary>
    /// Syncs a single repository to its default branch by calling the agent directly, so CommandOutput flows to TerminalSinkContext when called inside a background job.
    /// Persists the resulting state through <see cref="WorkspaceRepositoryStateWriter"/> but does not recompute workspace-wide stats or broadcast:
    /// the caller owns that boundary and must call <see cref="RecomputeAndBroadcastWorkspaceSyncedAsync"/> once after its whole batch, single-repository batches included.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> SyncToDefaultDirectAsync(
        int workspaceId,
        int repositoryId,
        string currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, "Workspace not found.");

        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
            return (false, "Repository not found.");

        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return (false, "Repository is not in the given workspace.");

        if (_connectorHealthService != null)
            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

        await _workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, cancellationToken);

        var wrWithPr = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(x => x.PullRequest)
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        var prInfo = wrWithPr?.PullRequest?.PullRequestNumber.HasValue == true
            ? wrWithPr.PullRequest.ToPullRequestInfo()
            : null;
        // "Delete local branches" is the user's own confirmation, given on a dialog that lists how many
        // commits each repository would lose and only enables Proceed after a countdown. Requiring a merged
        // or closed pull request on top of it made git fall back to "git branch -d", which refuses to delete
        // exactly the unmerged branches the dialog just promised to remove, so the branch survived the sync.
        // A merged or closed pull request stays an independent reason the branch is safe to drop.
        var forceDeleteLocalBranch = allowForceDeleteLocalBranch || prInfo?.IsMerged == true || prInfo?.IsClosed == true;

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var args = new
        {
            workspaceName = workspace.Name,
            repositoryName = repo.RepositoryName,
            currentBranchName,
            bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
            workspaceRoot,
            forceDeleteLocalBranch,
            deleteRemoteBranch
        };

        var response = await _agentBridge.SendCommandAsync("SyncToDefaultBranch", args, cancellationToken);
        var syncResponse = AgentResponseJson.DeserializeAgentResponse<SyncToDefaultBranchResponse>(response.Data);
        var commandSuccess = syncResponse?.Success ?? response.Success;
        var errorMessage = syncResponse?.ErrorMessage ?? response.Error ?? "Failed to sync to default branch";

        if (!commandSuccess)
            return (false, errorMessage);

        if (syncResponse?.LocalBranches == null)
        {
            // The agent reported no branch lists, so the writer cannot replace them. Remove at least the
            // branch that was just deleted locally.
            var toRemove = await _dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote && rb.BranchName == currentBranchName)
                .ToListAsync(cancellationToken);
            if (toRemove.Count > 0)
            {
                _dbContext.RepositoryBranches.RemoveRange(toRemove);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // One authoritative write of branch, version, counts, upstream, branch rows, projects and the PR
        // row, so no field of the previous branch survives the switch to the default branch.
        var snapshot = BuildSyncToDefaultSnapshot(syncResponse);
        await _stateWriter.ApplyAsync(workspaceId, repositoryId, snapshot, new RepositoryStateWriteOptions
        {
            SyncStatus = SyncStatusWrite.Derive,
            ReconcilePullRequest = true,
        }, cancellationToken);

        return (true, null);
    }

    /// <summary>
    /// Builds the state snapshot for a sync-to-default response. Newer agents send an explicit snapshot
    /// with probe markers; older ones send the flat fields, which are mapped here with the markers a
    /// successful sync-to-default is known to satisfy.
    /// </summary>
    private static RepositoryStateSnapshot BuildSyncToDefaultSnapshot(SyncToDefaultBranchResponse? syncResponse)
    {
        if (syncResponse == null)
            return new RepositoryStateSnapshot();

        if (syncResponse.State != null)
            return syncResponse.State;

        var branchesProbed = syncResponse.LocalBranches != null;
        return new RepositoryStateSnapshot
        {
            BranchName = syncResponse.CurrentBranch ?? syncResponse.DefaultBranch,
            CheckedOutTag = syncResponse.CurrentTag,
            GitVersion = syncResponse.GitVersion,
            DefaultBranchName = syncResponse.DefaultBranch,
            OutgoingCommits = syncResponse.OutgoingCommits,
            IncomingCommits = syncResponse.IncomingCommits,
            DefaultBranchBehind = syncResponse.DefaultBranchBehind,
            DefaultBranchAhead = syncResponse.DefaultBranchAhead,
            HasUpstream = syncResponse.HasUpstream,
            LocalBranches = syncResponse.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList(),
            RemoteBranches = syncResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? (branchesProbed ? [] : null),
            Tags = syncResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? (branchesProbed ? [] : null),
            Projects = syncResponse.Projects != null ? ToProjectNotifications(GetProjectsDetail(syncResponse.Projects)) ?? [] : null,
            IdentityProbed = true,
            GitVersionProbed = !string.IsNullOrWhiteSpace(syncResponse.GitVersion),
            // A pre-snapshot agent only reaches this point after a successful checkout and pull, at which
            // point it always ran both count queries and the upstream check.
            CommitCountsProbed = true,
            UpstreamProbed = syncResponse.HasUpstream.HasValue,
            BranchesProbed = branchesProbed,
            ProjectsProbed = syncResponse.Projects != null,
        };
    }

    /// <summary>Maps the App's project model onto the wire shape carried by <see cref="RepositoryStateSnapshot"/>. An empty (not null) input stays empty, since a probed empty scan is meaningful.</summary>
    private static List<RepositorySyncProjectNotification>? ToProjectNotifications(IReadOnlyList<SyncProjectInfo>? projects)
    {
        if (projects == null)
            return null;
        return projects
            .Select(p => new RepositorySyncProjectNotification
            {
                Name = p.ProjectName,
                ProjectType = (int)p.ProjectType,
                ProjectPath = p.ProjectFilePath,
                TargetFramework = p.TargetFramework,
                PackageId = p.PackageId,
                PackageReferences = (p.PackageReferences ?? [])
                    .Select(pr => new RepositorySyncPackageReferenceNotification { Name = pr.Name, Version = pr.Version })
                    .ToList()
            })
            .ToList();
    }

    /// <summary>Refreshes branches for a single repository by calling the agent directly. Routes CommandOutput to TerminalSinkContext when called within a background job.</summary>
    public async Task<bool> RefreshBranchesForRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null) return false;

        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null) return false;

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return false;

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await _agentBridge.SendCommandAsync("RefreshBranches", new
        {
            workspaceName = workspace.Name,
            repositoryId = repo.RepositoryId,
            repositoryName = repo.RepositoryName,
            workspaceRoot
        }, cancellationToken);

        if (!response.Success) return false;

        var refreshResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(response.Data);
        if (refreshResponse == null) return false;

        var localBranches = refreshResponse.LocalBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var remoteBranches = refreshResponse.RemoteBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var tags = refreshResponse.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        await PersistBranchesAsync(wr.WorkspaceRepositoryId, localBranches, remoteBranches, refreshResponse.DefaultBranch, tags, refreshResponse.CurrentTag, cancellationToken);

        // BranchHasUpstream is only written from the agent's own git-config probe. Deriving it here by
        // matching the branch name against the remote list said "has upstream" for any branch that merely
        // shares a name with a remote ref, which is how a freshly checked-out default branch could end up
        // with the upstream badge instead of its commit counts.
        if (refreshResponse.UpstreamProbed && string.IsNullOrWhiteSpace(refreshResponse.CurrentTag))
        {
            wr.BranchHasUpstream = refreshResponse.HasUpstream;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task RefreshBranchesAndBroadcastAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        await RefreshBranchesForRepositoryAsync(repositoryId, workspaceId, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken: cancellationToken);
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

        var (version, branch, tag, gitVersionError, gitFetchError, commandSucceeded) = GetVersionBranch(response.Data);
        var projectsCount = GetProjects(response.Data);
        var projectsDetail = GetProjectsDetail(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (localBranches, remoteBranches, defaultBranch, tags, currentTag) = GetBranches(response.Data);
        var (hasUpstream, upstreamProbed) = GetUpstream(response.Data);
        // Prefer Tag from the top-level response, fall back to currentTag from the branches block.
        var resolvedTag = !string.IsNullOrWhiteSpace(tag) ? tag : currentTag;
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);

        // The transport envelope only says the command ran; the payload says whether it did its job.
        // When it bailed out (a failed fetch, say) every field below is absent rather than genuinely
        // null, so nothing is marked probed and no column gets cleared on the strength of a null.
        var probed = commandSucceeded;
        var onTag = !string.IsNullOrWhiteSpace(resolvedTag);

        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Tag = resolvedTag,
            Tags = tags,
            Projects = projectsCount,
            ProjectsDetail = projectsDetail,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            DefaultBranchBehindCommits = defaultBehind,
            DefaultBranchAheadCommits = defaultAhead,
            HasUpstream = hasUpstream,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            DefaultBranch = defaultBranch,
            ErrorMessage = combinedError,
            Snapshot = new RepositoryStateSnapshot
            {
                BranchName = branch,
                CheckedOutTag = resolvedTag,
                GitVersion = version == "-" ? null : version,
                DefaultBranchName = defaultBranch,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                HasUpstream = onTag ? null : hasUpstream,
                LocalBranches = localBranches?.ToList(),
                RemoteBranches = remoteBranches?.ToList(),
                Tags = tags?.ToList(),
                Projects = HasProjectsBlock(response.Data) ? ToProjectNotifications(projectsDetail) ?? [] : null,
                ErrorMessage = combinedError,
                IdentityProbed = probed,
                GitVersionProbed = probed && version != "-",
                CommitCountsProbed = probed && !onTag,
                UpstreamProbed = probed && !onTag && upstreamProbed,
                BranchesProbed = probed && localBranches != null,
                ProjectsProbed = probed && HasProjectsBlock(response.Data),
            }
        };
    }

    private static RepoGitVersionInfo ParseRefreshRepositoryVersionResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch, tag, gitVersionError, gitFetchError, _) = GetVersionBranch(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (hasUpstream, remoteBranches, localBranches) = GetRefreshBranchesAndUpstream(response.Data);
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);
        var onTag = !string.IsNullOrWhiteSpace(tag);
        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Tag = tag,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            DefaultBranchBehindCommits = defaultBehind,
            DefaultBranchAheadCommits = defaultAhead,
            RemoteBranches = remoteBranches,
            LocalBranches = localBranches,
            ErrorMessage = combinedError,
            Snapshot = new RepositoryStateSnapshot
            {
                BranchName = branch,
                CheckedOutTag = tag,
                GitVersion = version == "-" ? null : version,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                // When pinned to a tag there is no branch to compare against.
                HasUpstream = onTag ? null : hasUpstream,
                RemoteBranches = remoteBranches?.ToList(),
                LocalBranches = localBranches?.ToList(),
                ErrorMessage = combinedError,
                IdentityProbed = true,
                GitVersionProbed = version != "-",
                CommitCountsProbed = !onTag,
                UpstreamProbed = !onTag && hasUpstream.HasValue,
                // This command lists branches but not tags, so it must not replace the persisted refs.
                BranchesProbed = false,
                ProjectsProbed = false,
            }
        };
    }

    /// <summary>Reads the agent's git-config upstream answer plus whether it actually resolved it, so an agent that omits both leaves the persisted flag alone.</summary>
    private static (bool? HasUpstream, bool UpstreamProbed) GetUpstream(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        return (r?.HasUpstream, r?.UpstreamProbed ?? false);
    }

    private static (bool? HasUpstream, IReadOnlyList<string>? RemoteBranches, IReadOnlyList<string>? LocalBranches) GetRefreshBranchesAndUpstream(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        return (r?.HasUpstream, remote?.Count > 0 ? remote : null, local?.Count > 0 ? local : null);
    }

    private static (string version, string branch, string? tag, string? gitVersionError, string? gitFetchError, bool commandSucceeded) GetVersionBranch(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        // Commands that do not report their own result are treated as successful, which is what they were before.
        var commandSucceeded = r?.Success ?? true;
        return (r?.Version ?? "-", r?.Branch ?? "-", string.IsNullOrWhiteSpace(r?.Tag) ? null : r!.Tag, r?.GitVersionError, r?.GitFetchError, commandSucceeded);
    }

    private static string? CombineRepoErrors(string? fetchError, string? versionError)
    {
        if (string.IsNullOrWhiteSpace(fetchError) && string.IsNullOrWhiteSpace(versionError))
            return null;
        if (string.IsNullOrWhiteSpace(fetchError))
            return versionError;
        if (string.IsNullOrWhiteSpace(versionError))
            return fetchError;
        return $"{fetchError.Trim()}. {versionError.Trim()}";
    }

    private static int? GetProjects(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        var projects = r?.Projects;
        if (projects == null) return null;
        return projects.Count > 0 ? projects.Count : null;
    }

    private static (int? Outgoing, int? Incoming, int? DefaultBehind, int? DefaultAhead) GetCommitCounts(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(data);
        return (r?.OutgoingCommits, r?.IncomingCommits, r?.DefaultBranchBehind, r?.DefaultBranchAhead);
    }

    private static (IReadOnlyList<string>? LocalBranches, IReadOnlyList<string>? RemoteBranches, string? DefaultBranch, IReadOnlyList<string>? Tags, string? CurrentTag) GetBranches(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentBranchesResponse>(data);
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var defaultBranch = !string.IsNullOrWhiteSpace(r?.DefaultBranch) ? r.DefaultBranch : null;
        var tags = r?.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var currentTag = !string.IsNullOrWhiteSpace(r?.CurrentTag) ? r.CurrentTag : null;
        return (local, remote, defaultBranch, tags, currentTag);
    }

    private static ProjectType? ComputeRepositoryType(IReadOnlyList<SyncProjectInfo>? projects)
    {
        if (projects == null || projects.Count == 0) return null;
        if (projects.Any(p => p.ProjectType == ProjectType.Service)) return ProjectType.Service;
        if (projects.Any(p => p.ProjectType == ProjectType.Package)) return ProjectType.Package;
        if (projects.Any(p => p.ProjectType == ProjectType.Executable)) return ProjectType.Executable;
        if (projects.Any(p => p.ProjectType == ProjectType.Library)) return ProjectType.Library;
        return ProjectType.Test;
    }

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        return GetProjectsDetail(r?.Projects);
    }

    /// <summary>
    /// Whether the response carried a project list at all. <see cref="GetProjectsDetail(object)"/> folds an
    /// empty list into null, which cannot be told apart from "the agent never scanned"; a probe marker needs
    /// exactly that distinction.
    /// </summary>
    private static bool HasProjectsBlock(object data)
        => AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data)?.Projects != null;

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(List<AgentProjectDto>? projects)
    {
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
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
    {
        var resultList = results.ToList();
        if (resultList.Count == 0) return;

        var repoIds = resultList.Select(r => r.RepoId).ToList();

        foreach (var (repoId, info) in resultList)
        {
            var snapshot = info.Snapshot ?? SnapshotFromFlatInfo(info);
            await _stateWriter.ApplyAsync(workspaceId, repoId, snapshot, new RepositoryStateWriteOptions
            {
                SyncStatus = SyncStatusWrite.Derive,
                ReconcilePullRequest = true,
            }, cancellationToken);
        }

        // The writer already merged each repository's projects; the dependency edges still have to be
        // merged as one batch so the level computation sees the whole graph at once.
        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, persistDependencyLevel, cancellationToken);

        // Partial sync (single repo or whole level): merge uses persistDependencyLevel false so Persist is not
        // called with a partial uniqueEdges graph. Recompute from full ProjectDependencies in DB so every
        // WorkspaceRepositoryLink gets correct DependencyLevel/Dependencies/UnmatchedDeps without syncing other repos.
        if (!persistDependencyLevel)
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }

    /// <summary>
    /// Fallback for <see cref="RepoGitVersionInfo"/> values built by code paths that do not yet produce a
    /// snapshot. Everything the flat shape carries is marked probed, which matches the merge behaviour those
    /// paths had before, and the groups it says nothing about stay untouched.
    /// </summary>
    private static RepositoryStateSnapshot SnapshotFromFlatInfo(RepoGitVersionInfo info)
    {
        var onTag = !string.IsNullOrWhiteSpace(info.Tag);
        return new RepositoryStateSnapshot
        {
            BranchName = info.Branch,
            CheckedOutTag = info.Tag,
            GitVersion = info.Version == "-" ? null : info.Version,
            DefaultBranchName = info.DefaultBranch,
            OutgoingCommits = info.OutgoingCommits,
            IncomingCommits = info.IncomingCommits,
            DefaultBranchBehind = info.DefaultBranchBehindCommits,
            DefaultBranchAhead = info.DefaultBranchAheadCommits,
            HasUpstream = info.HasUpstream,
            LocalBranches = info.LocalBranches?.ToList(),
            RemoteBranches = info.RemoteBranches?.ToList(),
            Tags = info.Tags?.ToList(),
            Projects = ToProjectNotifications(info.ProjectsDetail),
            ErrorMessage = info.ErrorMessage,
            IdentityProbed = true,
            GitVersionProbed = info.Version != "-",
            CommitCountsProbed = !onTag && (info.OutgoingCommits.HasValue || info.IncomingCommits.HasValue),
            UpstreamProbed = !onTag && info.HasUpstream.HasValue,
            BranchesProbed = info.LocalBranches != null || info.RemoteBranches != null || info.Tags != null,
            ProjectsProbed = info.ProjectsDetail != null,
        };
    }

    /// <summary>Persists branches for a workspace repository. Removes branches not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master).</summary>
    public Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName = null,
        CancellationToken cancellationToken = default)
        => PersistBranchesAsync(workspaceRepositoryId, localBranches, remoteBranches, defaultBranchName, tags: null, currentTag: null, cancellationToken);

    /// <summary>Persists branches and tags for a workspace repository. Removes branches/tags not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master) and the currently checked-out tag.</summary>
    public Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default)
        => _branchWriter.PersistAsync(workspaceRepositoryId, localBranches, remoteBranches, defaultBranchName, tags, currentTag, cancellationToken);

    /// <summary>Creates a new branch in all workspace repos (in parallel), then checks it out. baseBranch is "__default__" to use each repo's default, or a branch name. When <paramref name="repositoryIds"/> is set, only those repos are included. When <paramref name="syncState"/> is true, hooks are suppressed and the agent returns full state inline so the app can persist it without waiting for async hook syncs.</summary>
    public async Task CreateBranchesAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        Action<int, int>? onProgress = null,
        IReadOnlySet<int>? repositoryIds = null,
        bool syncState = false,
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

        if (repositoryIds != null && repositoryIds.Count > 0)
            links = links.Where(wr => repositoryIds.Contains(wr.RepositoryId)).ToList();

        if (links.Count == 0)
            return;

        var useDefaultBase = string.Equals(baseBranch, "__default__", StringComparison.OrdinalIgnoreCase);
        var completedCount = 0;
        var totalCount = links.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        // Prefetch all default branches before the parallel section to avoid concurrent DbContext reads
        Dictionary<int, string>? defaultBranchByWrId = null;
        if (useDefaultBase)
        {
            var wrIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
            var defaultRows = await _dbContext.RepositoryBranches
                .Where(rb => wrIds.Contains(rb.WorkspaceRepositoryId) && rb.IsDefault)
                .Select(rb => new { rb.WorkspaceRepositoryId, rb.BranchName })
                .ToListAsync(cancellationToken);
            defaultBranchByWrId = new Dictionary<int, string>();
            foreach (var row in defaultRows)
                defaultBranchByWrId.TryAdd(row.WorkspaceRepositoryId, row.BranchName);
        }

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
                    baseBranchName = defaultBranchByWrId?.GetValueOrDefault(wr.WorkspaceRepositoryId) ?? "main";
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
                    workspaceRoot,
                    repositoryId = wr.RepositoryId,
                    skipHooks = syncState
                };
                var response = await _agentBridge.SendCommandAsync("CreateBranch", args, cancellationToken);
                var createResponse = AgentResponseJson.DeserializeAgentResponse<CreateBranchResponse>(response.Data);
                var success = createResponse?.Success ?? response.Success;

                if (success)
                {
                    wr.BranchName = createResponse?.Branch ?? newBranchName;
                    if (syncState && createResponse != null)
                    {
                        // Hooks were suppressed — persist all state returned inline so the next
                        // step (dependency update) sees a complete, consistent database.
                        wr.CheckedOutTag = null;
                        if (createResponse.Version != null)
                            wr.GitVersion = createResponse.Version;
                        if (createResponse.OutgoingCommits.HasValue)
                            wr.OutgoingCommits = createResponse.OutgoingCommits;
                        if (createResponse.IncomingCommits.HasValue)
                            wr.IncomingCommits = createResponse.IncomingCommits;
                        if (createResponse.HasUpstream.HasValue)
                            wr.BranchHasUpstream = createResponse.HasUpstream;
                        if (createResponse.DefaultBranchBehind.HasValue)
                            wr.DefaultBranchBehindCommits = createResponse.DefaultBranchBehind;
                        if (createResponse.DefaultBranchAhead.HasValue)
                            wr.DefaultBranchAheadCommits = createResponse.DefaultBranchAhead;
                    }
                }
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
    public async Task EnsureLocalBranchPersistedAsync(int workspaceRepositoryId, string branchName, CancellationToken cancellationToken = default)
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

    /// <summary>Adds the given branch as a remote branch to persistence and sets BranchHasUpstream on the workspace repository link. Used after a successful push so the branch appears in Remotes without calling refresh branches.</summary>
    public async Task EnsureRemoteBranchPersistedAsync(int workspaceId, int repositoryId, string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return;
        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return;
        var remoteBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? branchName : "origin/" + branchName;
        var exists = await _dbContext.RepositoryBranches
            .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote && rb.BranchName == remoteBranchName, cancellationToken);
        if (!exists)
        {
            _dbContext.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                BranchName = remoteBranchName,
                IsRemote = true,
                LastSeenAt = DateTime.UtcNow,
                IsDefault = false
            });
        }
        wr.BranchHasUpstream = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
