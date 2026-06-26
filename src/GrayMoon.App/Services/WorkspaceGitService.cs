using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
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
        var count = await SyncDependenciesAsync(workspaceId, repoIdsToSync: new HashSet<int> { repositoryId }, onProgress: (c, t, _) => onProgressMessage?.Invoke($"Synced dependencies {c} of {t}"), onRepoError: onRepoError, cancellationToken: cancellationToken);
        await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
        _logger.LogDebug("RunUpdateSingleRepository completed for workspace {WorkspaceName}, repo {RepositoryId}, synced={Count}", workspace.Name, repositoryId, count);
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

        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        _logger.LogDebug("Sync dependencies completed for workspace {WorkspaceName}. Updated {RepoCount} repos, persisted {UpdateCount} versions", workspace.Name, toSync.Count, updatesToPersist.Count);
        return toSync.Count;
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

    /// <summary>Broadcasts WorkspaceSynced so the grid refreshes. Call after SyncDependenciesAsync (which already recomputes and persists UnmatchedDeps).</summary>
    public async Task RecomputeAndBroadcastWorkspaceSyncedAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    /// <summary>Stages updated .csproj paths and commits with message "chore(deps): update package versions" plus the full list of packages (one line per package: "- {packageId} to {version}"). Runs up to 8 commits in parallel.</summary>
    public async Task<IReadOnlyList<(int RepoId, string? ErrorMessage)>> CommitDependencyUpdatesAsync(
        int workspaceId,
        IReadOnlyList<SyncDependenciesRepoPayload> reposToCommit,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? commitMessageOverride = null,
        bool includeDepsInCommitMessage = true)
    {
        if (!_agentBridge.IsAgentConnected || reposToCommit.Count == 0)
            return Array.Empty<(int, string?)>();

        var tagPinnedIds = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();
        reposToCommit = reposToCommit.Where(r => !tagPinnedIds.Contains(r.RepoId)).ToList();
        if (reposToCommit.Count == 0)
            return Array.Empty<(int, string?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return reposToCommit.Select(r => (r.RepoId, (string?)"Workspace not found.")).ToList();

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
                var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data) is { Success: true };
                var err = success ? null : (response.Error ?? AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data!)?.ErrorMessage ?? "Commit failed");
                var c = Interlocked.Increment(ref completed);
                onProgress?.Invoke(c, total, repo.RepoId);
                return (RepoId: repo.RepoId, ErrorMessage: err);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        var byRepo = completedResults.ToDictionary(x => x.RepoId, x => x.ErrorMessage);
        return reposToCommit.Select(r => (r.RepoId, ErrorMessage: byRepo[r.RepoId])).ToList();
    }

    /// <summary>Stages the given file paths per repo and commits with message "chore(deps): update versions (N)" where N is the path count for that repo. Uses the same agent StageAndCommit command.</summary>
    public async Task<IReadOnlyList<(int RepoId, string? ErrorMessage)>> CommitFilePathsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> reposAndPaths,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? commitMessageOverride = null)
    {
        if (!_agentBridge.IsAgentConnected || reposAndPaths.Count == 0)
            return Array.Empty<(int, string?)>();

        var tagPinnedIdsFp = (await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken)).ToHashSet();
        reposAndPaths = reposAndPaths.Where(r => !tagPinnedIdsFp.Contains(r.RepoId)).ToList();
        if (reposAndPaths.Count == 0)
            return Array.Empty<(int, string?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return reposAndPaths.Select(r => (r.RepoId, (string?)"Workspace not found.")).ToList();

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
                    return (RepoId: repo.RepoId, ErrorMessage: (string?)"No paths to stage.");
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
                var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data) is { Success: true };
                var err = success ? null : (response.Error ?? AgentResponseJson.DeserializeAgentResponse<StageAndCommitResponse>(response.Data!)?.ErrorMessage ?? "Commit failed");
                var c = Interlocked.Increment(ref completed);
                onProgress?.Invoke(c, total, repo.RepoId);
                return (RepoId: repo.RepoId, ErrorMessage: err);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        var byRepo = completedResults.ToDictionary(x => x.RepoId, x => x.ErrorMessage);
        return reposAndPaths.Select(r => (r.RepoId, ErrorMessage: byRepo[r.RepoId])).ToList();
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

    /// <summary>Syncs a single repository to its default branch by calling the agent directly, so CommandOutput flows to TerminalSinkContext when called inside a background job.</summary>
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
        var forceDeleteLocalBranch = allowForceDeleteLocalBranch && (prInfo?.IsMerged == true || prInfo?.IsClosed == true);

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

        if (syncResponse?.LocalBranches != null)
        {
            var localBranches = syncResponse.LocalBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            var remoteBranches = syncResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
            var tags = syncResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
            await PersistBranchesAsync(wr.WorkspaceRepositoryId, localBranches, remoteBranches, syncResponse.DefaultBranch, tags, syncResponse.CurrentTag, cancellationToken);
        }
        else
        {
            var toRemove = await _dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote && rb.BranchName == currentBranchName)
                .ToListAsync(cancellationToken);
            if (toRemove.Count > 0)
            {
                _dbContext.RepositoryBranches.RemoveRange(toRemove);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // Persist BranchName and commit counts from the agent response so the UI is up to date
        // immediately without waiting for the async post-merge hook (which only fires when pull merges).
        if (syncResponse != null)
        {
            wr.BranchName = syncResponse.CurrentBranch ?? syncResponse.DefaultBranch;
            wr.CheckedOutTag = null;
            if (syncResponse.OutgoingCommits.HasValue) wr.OutgoingCommits = syncResponse.OutgoingCommits;
            if (syncResponse.IncomingCommits.HasValue) wr.IncomingCommits = syncResponse.IncomingCommits;
            if (syncResponse.HasUpstream.HasValue) wr.BranchHasUpstream = syncResponse.HasUpstream.Value;
            if (syncResponse.DefaultBranchBehind.HasValue) wr.DefaultBranchBehindCommits = syncResponse.DefaultBranchBehind;
            if (syncResponse.DefaultBranchAhead.HasValue) wr.DefaultBranchAheadCommits = syncResponse.DefaultBranchAhead;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

        return (true, null);
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

        if (string.IsNullOrWhiteSpace(refreshResponse.CurrentTag))
        {
            var branch = refreshResponse.CurrentBranch?.Trim();
            if (!string.IsNullOrWhiteSpace(branch))
            {
                var hasUpstream = remoteBranches.Any(r => !string.IsNullOrEmpty(r) &&
                    (string.Equals(r, branch, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r, "origin/" + branch, StringComparison.OrdinalIgnoreCase)
                     || r.EndsWith("/" + branch, StringComparison.OrdinalIgnoreCase)));
                wr.BranchHasUpstream = hasUpstream;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
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

        var (version, branch, tag, gitVersionError, gitFetchError) = GetVersionBranch(response.Data);
        var projectsCount = GetProjects(response.Data);
        var projectsDetail = GetProjectsDetail(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (localBranches, remoteBranches, defaultBranch, tags, currentTag) = GetBranches(response.Data);
        // Prefer Tag from the top-level response, fall back to currentTag from the branches block.
        var resolvedTag = !string.IsNullOrWhiteSpace(tag) ? tag : currentTag;
        var hasUpstream = string.IsNullOrWhiteSpace(resolvedTag) ? ComputeHasUpstream(branch, remoteBranches) : null;
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);
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
            ErrorMessage = combinedError
        };
    }

    private static bool? ComputeHasUpstream(string? branch, IReadOnlyList<string>? remoteBranches)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch == "-" || remoteBranches == null || remoteBranches.Count == 0)
            return null;
        return remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
    }

    private static RepoGitVersionInfo ParseRefreshRepositoryVersionResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch, tag, gitVersionError, gitFetchError) = GetVersionBranch(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (hasUpstream, remoteBranches, localBranches) = GetRefreshBranchesAndUpstream(response.Data);
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);
        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Tag = tag,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            DefaultBranchBehindCommits = defaultBehind,
            DefaultBranchAheadCommits = defaultAhead,
            // When pinned to a tag, suppress upstream computation (no branch to compare against).
            HasUpstream = string.IsNullOrWhiteSpace(tag) ? hasUpstream : null,
            RemoteBranches = remoteBranches,
            LocalBranches = localBranches,
            ErrorMessage = combinedError
        };
    }

    private static (bool? HasUpstream, IReadOnlyList<string>? RemoteBranches, IReadOnlyList<string>? LocalBranches) GetRefreshBranchesAndUpstream(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        return (r?.HasUpstream, remote?.Count > 0 ? remote : null, local?.Count > 0 ? local : null);
    }

    private static (string version, string branch, string? tag, string? gitVersionError, string? gitFetchError) GetVersionBranch(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        return (r?.Version ?? "-", r?.Branch ?? "-", string.IsNullOrWhiteSpace(r?.Tag) ? null : r!.Tag, r?.GitVersionError, r?.GitFetchError);
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
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
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

                if (!string.IsNullOrWhiteSpace(info.Tag))
                {
                    // Repo is pinned to a tag (detached HEAD): clear branch-only fields so the UI does
                    // not render misleading divergence / "push to set upstream" badges.
                    wr.CheckedOutTag = info.Tag;
                    wr.BranchName = null;
                    wr.BranchHasUpstream = null;
                    wr.OutgoingCommits = null;
                    wr.IncomingCommits = null;
                    wr.DefaultBranchBehindCommits = null;
                    wr.DefaultBranchAheadCommits = null;
                }
                else
                {
                    wr.CheckedOutTag = null;
                    if (info.OutgoingCommits.HasValue) wr.OutgoingCommits = info.OutgoingCommits;
                    if (info.IncomingCommits.HasValue) wr.IncomingCommits = info.IncomingCommits;
                    if (info.HasUpstream.HasValue) wr.BranchHasUpstream = info.HasUpstream.Value;
                    if (info.DefaultBranchBehindCommits.HasValue) wr.DefaultBranchBehindCommits = info.DefaultBranchBehindCommits;
                    if (info.DefaultBranchAheadCommits.HasValue) wr.DefaultBranchAheadCommits = info.DefaultBranchAheadCommits;
                }

                if (!string.IsNullOrWhiteSpace(info.DefaultBranch))
                    wr.DefaultBranchName = info.DefaultBranch;
                if (info.Projects.HasValue) wr.Projects = info.Projects;
                var hasValidVersion = info.Version != "-" && (info.Branch != "-" || !string.IsNullOrWhiteSpace(info.Tag));
                var hasDefaultBranch = !string.IsNullOrWhiteSpace(wr.DefaultBranchName);
                wr.SyncStatus = !hasValidVersion
                    ? RepoSyncStatus.Error
                    : (hasDefaultBranch ? RepoSyncStatus.InSync : RepoSyncStatus.NeedsSync);
            }

            if (info.ProjectsDetail is { Count: > 0 })
            {
                await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repoId, info.ProjectsDetail, cancellationToken);
                if (wr != null)
                    wr.RepositoryType = ComputeRepositoryType(info.ProjectsDetail);
            }

            // Persist branches if available (include default branch so IsDefault is set, and tags so the picker can show them)
            if ((info.LocalBranches != null || info.RemoteBranches != null || info.Tags != null) && wr != null)
            {
                await PersistBranchesAsync(wr.WorkspaceRepositoryId, info.LocalBranches, info.RemoteBranches, info.DefaultBranch, info.Tags, info.Tag, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, persistDependencyLevel, cancellationToken);

        // Partial sync (single repo or whole level): merge uses persistDependencyLevel false so Persist is not
        // called with a partial uniqueEdges graph. Recompute from full ProjectDependencies in DB so every
        // WorkspaceRepositoryLink gets correct DependencyLevel/Dependencies/UnmatchedDeps without syncing other repos.
        if (!persistDependencyLevel)
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);

        await _workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, repoIds, cancellationToken: cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }

    /// <summary>Persists branches for a workspace repository. Removes branches not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master).</summary>
    public Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName = null,
        CancellationToken cancellationToken = default)
        => PersistBranchesAsync(workspaceRepositoryId, localBranches, remoteBranches, defaultBranchName, tags: null, currentTag: null, cancellationToken);

    /// <summary>True when a ref name is one of git's synthetic placeholders that appear in detached HEAD state (e.g. <c>(HEAD detached at v1.0)</c>, <c>(no branch)</c>, <c>origin/(no branch)</c>). These are not real branches and must never be persisted.</summary>
    private static bool IsSyntheticGitRef(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return true;
        if (trimmed.StartsWith("(", StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
            return true;
        if (trimmed.EndsWith("/(no branch)", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>Persists branches and tags for a workspace repository. Removes branches/tags not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master) and the currently checked-out tag.</summary>
    public async Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existingBranches = await _dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId)
            .ToListAsync(cancellationToken);

        var fetchedRefs = new HashSet<(string Name, bool IsRemote, bool IsTag)>();
        // Tracks the agent-provided rank for tags so we can persist "newest first" order; branches default to 0.
        var sortIndexByRef = new Dictionary<(string Name, bool IsRemote, bool IsTag), int>();
        if (localBranches != null)
        {
            foreach (var branch in localBranches)
            {
                if (!string.IsNullOrWhiteSpace(branch) && !IsSyntheticGitRef(branch))
                    fetchedRefs.Add((branch, false, false));
            }
        }
        if (remoteBranches != null)
        {
            foreach (var branch in remoteBranches)
            {
                if (!string.IsNullOrWhiteSpace(branch) && !IsSyntheticGitRef(branch))
                    fetchedRefs.Add((branch, true, false));
            }
        }
        if (tags != null)
        {
            var rank = 0;
            foreach (var tag in tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    var key = (tag, false, true);
                    if (fetchedRefs.Add(key))
                        sortIndexByRef[key] = rank++;
                }
            }
        }

        // Clear IsDefault for all existing; we will set it for the default branch below
        foreach (var b in existingBranches)
            b.IsDefault = false;

        // Update existing rows or add new ones
        foreach (var (name, isRemote, isTag) in fetchedRefs)
        {
            var isDefault = !isTag && !string.IsNullOrWhiteSpace(defaultBranchName) && string.Equals(name, defaultBranchName, StringComparison.OrdinalIgnoreCase);
            var sortIndex = sortIndexByRef.TryGetValue((name, isRemote, isTag), out var rank) ? rank : 0;
            var existing = existingBranches.FirstOrDefault(b => b.BranchName == name && b.IsRemote == isRemote && b.IsTag == isTag);
            if (existing != null)
            {
                existing.LastSeenAt = now;
                existing.IsDefault = isDefault;
                if (isTag)
                    existing.SortIndex = sortIndex;
            }
            else
            {
                _dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = workspaceRepositoryId,
                    BranchName = name,
                    IsRemote = isRemote,
                    IsTag = isTag,
                    LastSeenAt = now,
                    IsDefault = isDefault,
                    SortIndex = isTag ? sortIndex : 0
                });
            }
        }

        // Remove rows that were not fetched (no longer exist). Tags are removed only when a tag list was
        // provided so callers that pass only branches (e.g. legacy paths) do not wipe persisted tags.
        var toRemove = existingBranches
            .Where(b => !fetchedRefs.Contains((b.BranchName, b.IsRemote, b.IsTag)))
            .Where(b => !b.IsTag || tags != null)
            .Where(b => b.IsTag || (localBranches != null || remoteBranches != null))
            .ToList();
        if (toRemove.Count > 0)
        {
            _dbContext.RepositoryBranches.RemoveRange(toRemove);
        }

        // Update WorkspaceRepositoryLink.CheckedOutTag from the agent-reported value when tags were refreshed.
        if (tags != null)
        {
            var link = await _dbContext.WorkspaceRepositories
                .FirstOrDefaultAsync(wr => wr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
            if (link != null)
            {
                if (!string.IsNullOrWhiteSpace(currentTag))
                {
                    link.CheckedOutTag = currentTag;
                    link.BranchName = null;
                    link.BranchHasUpstream = null;
                    link.OutgoingCommits = null;
                    link.IncomingCommits = null;
                    link.DefaultBranchBehindCommits = null;
                    link.DefaultBranchAheadCommits = null;
                    // Determine if a newer tag exists: SortIndex 0 = newest. If currentTag is not at index 0, there is a newer tag.
                    var tagIdx = -1;
                    for (var i = 0; i < tags.Count; i++)
                    {
                        if (string.Equals(tags[i], currentTag, StringComparison.OrdinalIgnoreCase))
                        { tagIdx = i; break; }
                    }
                    link.HasNewerTag = tagIdx > 0;
                }
                else if (!string.IsNullOrWhiteSpace(link.CheckedOutTag))
                {
                    // Tag list refreshed but we are no longer on a tag; clear the pinned state.
                    link.CheckedOutTag = null;
                    link.HasNewerTag = null;
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

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
