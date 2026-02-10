using System.Text.Json;
using GrayMoon.App.Data;
using Microsoft.AspNetCore.SignalR;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
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
                var projectsDetail = response.Success && response.Data != null ? GetProjectsDetail(response.Data) : null;
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount, repo.RepositoryId);
                return (repo.RepositoryId, ProjectsDetail: projectsDetail);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        foreach (var (repoId, projectsDetail) in syncResults)
        {
            if (projectsDetail is { Count: > 0 })
                await _workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repoId, projectsDetail, cancellationToken);
        }

        var resultsForDeps = syncResults.Select(r => (r.RepositoryId, r.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, resultsForDeps, cancellationToken);

        _logger.LogDebug("RefreshWorkspaceProjects completed for workspace {WorkspaceName}", workspace.Name);
    }

    /// <summary>Syncs dependency versions in .csproj files to match the current version of each referenced package source. Only repos with at least one mismatched dependency are updated.</summary>
    public async Task<int> SyncDependenciesAsync(
        int workspaceId,
        Action<int, int, int>? onProgress = null,
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
                throw new InvalidOperationException(response.Error ?? "SyncRepositoryDependencies failed.");

            var count = Interlocked.Increment(ref completedCount);
            onProgress?.Invoke(count, totalCount, repo.RepoId);
        }

        var updatesToPersist = toSync
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
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch) = GetVersionBranch(response.Data);
        var projectsCount = GetProjects(response.Data);
        var projectsDetail = GetProjectsDetail(response.Data);
        var (outgoingCommits, incomingCommits) = GetCommitCounts(response.Data);
        return new RepoGitVersionInfo { Version = version, Branch = branch, Projects = projectsCount, ProjectsDetail = projectsDetail, OutgoingCommits = outgoingCommits, IncomingCommits = incomingCommits };
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
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "-" : "-";
        var branch = root.TryGetProperty("branch", out var b) ? b.GetString() ?? "-" : "-";
        return (version, branch);
    }

    private static int? GetProjects(object data)
    {
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("projects", out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Array)
            return p.GetArrayLength();
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
            return n;
        return null;
    }

    private static (int? Outgoing, int? Incoming) GetCommitCounts(object data)
    {
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var outgoing = root.TryGetProperty("outgoingCommits", out var o) && o.TryGetInt32(out var outVal) ? outVal : (int?)null;
        var incoming = root.TryGetProperty("incomingCommits", out var i) && i.TryGetInt32(out var inVal) ? inVal : (int?)null;
        return (outgoing, incoming);
    }

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(object data)
    {
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("projects", out var p) || p.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<SyncProjectInfo>();
        foreach (var el in p.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var projectType = ProjectType.Library;
            if (el.TryGetProperty("projectType", out var pt) && pt.TryGetInt32(out var ptVal) && ptVal >= 0 && ptVal <= 4)
                projectType = (ProjectType)ptVal;
            var projectPath = el.TryGetProperty("projectPath", out var pp) ? pp.GetString() ?? "" : "";
            var targetFramework = el.TryGetProperty("targetFramework", out var tf) ? tf.GetString() ?? "" : "";
            var packageId = el.TryGetProperty("packageId", out var pi) ? pi.GetString() : null;
            var packageRefs = ParsePackageReferences(el);
            list.Add(new SyncProjectInfo(name, projectType, projectPath, targetFramework, packageId, packageRefs));
        }
        return list.Count > 0 ? list : null;
    }

    private static IReadOnlyList<SyncPackageReference> ParsePackageReferences(JsonElement projectEl)
    {
        if (!projectEl.TryGetProperty("packageReferences", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<SyncPackageReference>();
        var list = new List<SyncPackageReference>();
        foreach (var refEl in arr.EnumerateArray())
        {
            var refName = refEl.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(refName)) continue;
            var version = refEl.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            list.Add(new SyncPackageReference(refName.Trim(), version));
        }
        return list;
    }

    private static RepoSyncStatus ParseGetRepositoryVersionToStatus(object data, string? persistedVersion, string? persistedBranch)
    {
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var exists = root.TryGetProperty("exists", out var e) && e.GetBoolean();
        if (!exists)
            return RepoSyncStatus.NotCloned;

        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var branch = root.TryGetProperty("branch", out var b) ? b.GetString() : null;
        if (version == null || branch == null)
            return RepoSyncStatus.VersionMismatch;

        return (version == persistedVersion && branch == persistedBranch) ? RepoSyncStatus.InSync : RepoSyncStatus.VersionMismatch;
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
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }
}
