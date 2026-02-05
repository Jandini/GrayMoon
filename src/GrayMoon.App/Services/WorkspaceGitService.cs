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
    RepositoryRepository repositoryRepository,
    RepositoryProjectRepository repositoryProjectRepository,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceGitService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null)
{
    private readonly IAgentBridge _agentBridge = agentBridge ?? throw new ArgumentNullException(nameof(agentBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly RepositoryRepository _repositoryRepository = repositoryRepository ?? throw new ArgumentNullException(nameof(repositoryRepository));
    private readonly RepositoryProjectRepository _repositoryProjectRepository = repositoryProjectRepository ?? throw new ArgumentNullException(nameof(repositoryProjectRepository));
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

    public async Task<bool> SyncSingleRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
        {
            _logger.LogWarning("Sync skipped: repository not found for id {RepositoryId}", repositoryId);
            return false;
        }

        var isInWorkspace = await _dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.LinkedRepositoryId == repo.RepositoryId, cancellationToken);
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
        return new RepoGitVersionInfo { Version = version, Branch = branch, Projects = projectsCount, ProjectsDetail = projectsDetail };
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
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.LinkedRepositoryId))
            .ToListAsync(cancellationToken);

        var reposToUpdate = await _dbContext.Repositories
            .Where(r => repoIds.Contains(r.RepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var (repoId, info) in resultList)
        {
            var wr = workspaceReposToUpdate.FirstOrDefault(w => w.LinkedRepositoryId == repoId);
            if (wr != null)
            {
                wr.GitVersion = info.Version == "-" ? null : info.Version;
                wr.BranchName = info.Branch == "-" ? null : info.Branch;
                wr.Projects = info.Projects;
                wr.SyncStatus = (info.Version == "-" || info.Branch == "-") ? RepoSyncStatus.Error : RepoSyncStatus.InSync;
            }

            var repo = reposToUpdate.FirstOrDefault(r => r.RepositoryId == repoId);
            if (repo != null)
                repo.ProjectCount = info.Projects;

            if (info.ProjectsDetail is { Count: > 0 })
                await _repositoryProjectRepository.MergeRepositoryProjectsAsync(repoId, info.ProjectsDetail, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _repositoryProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }
}
