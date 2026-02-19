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
    IHubContext<WorkspaceSyncHub>? hubContext = null)
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

    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        Action<int, int, int, RepoGitVersionInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to sync repositories.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        await _workspaceService.CreateDirectoryAsync(workspace.Name, cancellationToken);

        var repos = workspace.Repositories
            .Select(link => link.Repository)
            .Where(r => r != null)
            .Cast<Repository>()
            .ToList();

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
                    workspaceId
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

        var isInSync = results.All(r => r.info.Version != "-" && r.info.Branch != "-");
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

        var syncResults = await Task.WhenAll(repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName };
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

    /// <summary>Syncs dependency versions in .csproj files to match the current version of each referenced package source. Only repos with at least one mismatched dependency are updated.</summary>
    public async Task<int> SyncDependenciesAsync(
        int workspaceId,
        Action<int, int, int>? onProgress = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to sync dependencies.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var payloads = await _workspaceProjectRepository.GetSyncDependenciesPayloadAsync(workspaceId, cancellationToken);
        var toSync = payloads.Where(p => p.ProjectUpdates.Count > 0).ToList();
        if (toSync.Count == 0)
        {
            _logger.LogInformation("Sync dependencies: no mismatched dependencies for workspace {WorkspaceName}", workspace.Name);
            return 0;
        }

        _logger.LogInformation("Sync dependencies: Workspace={WorkspaceName}, RepoCount={RepoCount}", workspace.Name, toSync.Count);

        var completedCount = 0;
        var totalCount = toSync.Count;
        var failedRepoIds = new HashSet<int>();

        foreach (var repo in toSync)
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
                projectUpdates
            };

            var response = await _agentBridge.SendCommandAsync("SyncRepositoryDependencies", args, cancellationToken);
            if (!response.Success)
            {
                failedRepoIds.Add(repo.RepoId);
                onRepoError?.Invoke(repo.RepoId, response.Error ?? "Sync dependencies failed");
                var c = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(c, totalCount, repo.RepoId);
                continue;
            }

            var c2 = Interlocked.Increment(ref completedCount);
            onProgress?.Invoke(c2, totalCount, repo.RepoId);
        }

        var updatesToPersist = toSync
            .Where(r => !failedRepoIds.Contains(r.RepoId))
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

    public async Task<bool> SyncSingleRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
        {
            _logger.LogWarning("Sync skipped: repository not found for id {RepositoryId}", repositoryId);
            return false;
        }

        var isInWorkspace = await _dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repo.RepositoryId, cancellationToken);
        if (!isInWorkspace)
        {
            _logger.LogWarning("Sync skipped: repository {RepositoryName} (id {RepositoryId}) is not linked to workspace {WorkspaceId}", repo.RepositoryName, repositoryId, workspaceId);
            return false;
        }

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return false;

        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryVersion", new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName }, cancellationToken);
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
        return true;
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

        foreach (var wr in workspaceRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repo = wr.Repository;
            if (repo == null) continue;

            var response = await _agentBridge.SendCommandAsync("GetRepositoryVersion", new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName }, cancellationToken);
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

        var (version, branch) = GetVersionBranch(response.Data);
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
            ErrorMessage = null
        };
    }

    private static RepoGitVersionInfo ParseRefreshRepositoryVersionResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch) = GetVersionBranch(response.Data);
        return new RepoGitVersionInfo { Version = version, Branch = branch };
    }

    private static (string version, string branch) GetVersionBranch(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        return (r?.Version ?? "-", r?.Branch ?? "-");
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
                    baseBranchName
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
        _hubContext?.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
    }
}
