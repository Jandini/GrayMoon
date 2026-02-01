using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

public class WorkspaceGitService
{
    private readonly GitCommandService _gitCommandService;
    private readonly GitVersionCommandService _gitVersionCommandService;
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceRepository _workspaceRepository;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<WorkspaceGitService> _logger;
    private readonly int _maxConcurrent;

    public WorkspaceGitService(
        GitCommandService gitCommandService,
        GitVersionCommandService gitVersionCommandService,
        WorkspaceService workspaceService,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILogger<WorkspaceGitService> logger)
    {
        _gitCommandService = gitCommandService ?? throw new ArgumentNullException(nameof(gitCommandService));
        _gitVersionCommandService = gitVersionCommandService ?? throw new ArgumentNullException(nameof(gitVersionCommandService));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var max = workspaceOptions?.Value?.MaxConcurrentGitOperations ?? 8;
        _maxConcurrent = max < 1 ? 1 : max;
    }

    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        Action<int, int, int, RepoGitVersionInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");
        }

        var workspacePath = _workspaceService.GetWorkspacePath(workspace.Name);
        _workspaceService.CreateDirectory(workspace.Name);

        var repos = workspace.Repositories
            .Select(link => link.GitHubRepository)
            .Where(r => r != null)
            .Cast<GitHubRepository>()
            .ToList();

        if (repos.Count == 0)
        {
            return new Dictionary<int, RepoGitVersionInfo>();
        }

        _logger.LogInformation("User triggered sync for workspace {WorkspaceName} ({RepoCount} repositories)", workspace.Name, repos.Count);
        _logger.LogDebug("Starting sync for workspace {WorkspaceName} ({RepoCount} repositories)", workspace.Name, repos.Count);

        var completedCount = 0;
        var totalCount = repos.Count;

        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var syncTasks = repos.Select(async repo =>
        {
            var result = await RunSyncJobAsync(repo, workspacePath, semaphore, cancellationToken);
            var count = Interlocked.Increment(ref completedCount);
            onProgress?.Invoke(count, totalCount, result.RepoId, result.Info);
            return result;
        });
        var results = await Task.WhenAll(syncTasks);

        await PersistVersionsAsync(workspaceId, results, cancellationToken);

        var isInSync = results.All(r => r.Info.Version != "-" && r.Info.Branch != "-");
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        _logger.LogDebug("Sync completed for workspace {WorkspaceName}", workspace.Name);
        return results.ToDictionary(r => r.RepoId, r => r.Info);
    }

    public async Task<bool> IsInSyncAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            return true;
        }

        var workspacePath = _workspaceService.GetWorkspacePath(workspace.Name);

        var workspaceRepos = workspace.Repositories.ToList();

        if (workspaceRepos.Count == 0)
        {
            return true;
        }

        _logger.LogDebug("Checking sync status for workspace {WorkspaceName} ({RepoCount} repositories)", workspace.Name, workspaceRepos.Count);

        foreach (var wr in workspaceRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repo = wr.GitHubRepository;
            if (repo == null) continue;

            var repoPath = Path.Combine(workspacePath, repo.RepositoryName);

            if (!Directory.Exists(repoPath))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Repository {RepoName} is not cloned", repo.RepositoryName);
                }
                return false;
            }

            var gitVersion = await _gitVersionCommandService.GetVersionAsync(repoPath, useCacheIfAvailable: true, cancellationToken);
            if (gitVersion == null)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Repository {RepoName} failed to get version", repo.RepositoryName);
                }
                return false;
            }

            var diskVersion = gitVersion.SemVer ?? gitVersion.FullSemVer;
            var diskBranch = gitVersion.BranchName ?? gitVersion.EscapedBranchName;

            if (diskVersion != wr.GitVersion || diskBranch != wr.BranchName)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Repository {RepoName} version/branch mismatch. Disk: {DiskVersion}/{DiskBranch}, Persisted: {PersistedVersion}/{PersistedBranch}",
                        repo.RepositoryName, diskVersion, diskBranch, wr.GitVersion, wr.BranchName);
                }
                return false;
            }
        }

        _logger.LogDebug("Workspace {WorkspaceName} is in sync", workspace.Name);
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
        {
            return result;
        }

        var workspacePath = _workspaceService.GetWorkspacePath(workspace.Name);

        var workspaceRepos = workspace.Repositories.ToList();

        if (workspaceRepos.Count == 0)
        {
            return result;
        }

        foreach (var wr in workspaceRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repo = wr.GitHubRepository;
            if (repo == null) continue;

            var repoPath = Path.Combine(workspacePath, repo.RepositoryName);
            RepoSyncStatus status;

            if (!Directory.Exists(repoPath))
            {
                status = RepoSyncStatus.NotCloned;
            }
            else
            {
                var gitVersion = await _gitVersionCommandService.GetVersionAsync(repoPath, useCacheIfAvailable: true, cancellationToken);
                if (gitVersion == null)
                {
                    status = RepoSyncStatus.VersionMismatch;
                }
                else
                {
                    var diskVersion = gitVersion.SemVer ?? gitVersion.FullSemVer;
                    var diskBranch = gitVersion.BranchName ?? gitVersion.EscapedBranchName;
                    status = diskVersion == wr.GitVersion && diskBranch == wr.BranchName
                        ? RepoSyncStatus.InSync
                        : RepoSyncStatus.VersionMismatch;
                }
            }

            result[repo.GitHubRepositoryId] = status;
            onProgress?.Invoke(repo.GitHubRepositoryId, status);
        }

        var isInSync = result.Values.All(v => v == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateIsInSyncAsync(workspaceId, isInSync);

        return result;
    }

    private async Task PersistVersionsAsync(
        int workspaceId,
        IEnumerable<(int RepoId, RepoGitVersionInfo Info)> results,
        CancellationToken cancellationToken)
    {
        var updates = results.Where(r => r.Info.Version != "-" || r.Info.Branch != "-").ToList();
        if (updates.Count == 0)
        {
            return;
        }

        var repoIds = updates.Select(u => u.RepoId).ToList();
        var workspaceReposToUpdate = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.GitHubRepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var update in updates)
        {
            var wr = workspaceReposToUpdate.FirstOrDefault(wr => wr.GitHubRepositoryId == update.RepoId);
            if (wr != null)
            {
                wr.GitVersion = update.Info.Version == "-" ? null : update.Info.Version;
                wr.BranchName = update.Info.Branch == "-" ? null : update.Info.Branch;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int RepoId, RepoGitVersionInfo Info)> RunSyncJobAsync(
        GitHubRepository repo,
        string workspacePath,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var repoPath = Path.Combine(workspacePath, repo.RepositoryName);

            if (!Directory.Exists(repoPath))
            {
                if (!string.IsNullOrWhiteSpace(repo.CloneUrl))
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Cloning {RepositoryName}", repo.RepositoryName);
                    }
                    await _gitCommandService.CloneAsync(workspacePath, repo.CloneUrl, cancellationToken);
                }
            }

            var version = "-";
            var branch = "-";
            if (Directory.Exists(repoPath))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Getting version for {RepositoryName}", repo.RepositoryName);
                }
                var gitVersion = await _gitVersionCommandService.GetVersionAsync(repoPath, cancellationToken);
                if (gitVersion != null)
                {
                    version = gitVersion.SemVer ?? gitVersion.FullSemVer ?? "-";
                    branch = gitVersion.BranchName ?? gitVersion.EscapedBranchName ?? "-";
                }
            }

            return (repo.GitHubRepositoryId, new RepoGitVersionInfo { Version = version, Branch = branch });
        }
        finally
        {
            semaphore.Release();
        }
    }
}
