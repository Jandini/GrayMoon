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

    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(int workspaceId, CancellationToken cancellationToken = default)
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

        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var syncTasks = repos.Select(repo => RunSyncJobAsync(repo, workspacePath, semaphore, cancellationToken));
        var results = await Task.WhenAll(syncTasks);

        await PersistVersionsAsync(results, cancellationToken);

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

        var repos = workspace.Repositories
            .Select(link => link.GitHubRepository)
            .Where(r => r != null)
            .Cast<GitHubRepository>()
            .ToList();

        if (repos.Count == 0)
        {
            return true;
        }

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repoPath = Path.Combine(workspacePath, repo.RepositoryName);

            if (!Directory.Exists(repoPath))
            {
                _logger.LogDebug("Repository {RepoName} is not cloned", repo.RepositoryName);
                return false;
            }

            var gitVersion = await _gitVersionCommandService.GetVersionAsync(repoPath, cancellationToken);
            if (gitVersion == null)
            {
                _logger.LogDebug("Repository {RepoName} failed to get version", repo.RepositoryName);
                return false;
            }

            var diskVersion = gitVersion.SemVer ?? gitVersion.FullSemVer;
            var diskBranch = gitVersion.BranchName ?? gitVersion.EscapedBranchName;

            if (diskVersion != repo.GitVersion || diskBranch != repo.BranchName)
            {
                _logger.LogDebug("Repository {RepoName} version/branch mismatch. Disk: {DiskVersion}/{DiskBranch}, Persisted: {PersistedVersion}/{PersistedBranch}",
                    repo.RepositoryName, diskVersion, diskBranch, repo.GitVersion, repo.BranchName);
                return false;
            }
        }

        return true;
    }

    private async Task PersistVersionsAsync(
        IEnumerable<(int RepoId, RepoGitVersionInfo Info)> results,
        CancellationToken cancellationToken)
    {
        var updates = results.Where(r => r.Info.Version != "-" || r.Info.Branch != "-").ToList();
        if (updates.Count == 0)
        {
            return;
        }

        var repoIds = updates.Select(u => u.RepoId).ToList();
        var repositoriesToUpdate = await _dbContext.GitHubRepositories
            .Where(r => repoIds.Contains(r.GitHubRepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var update in updates)
        {
            var repo = repositoriesToUpdate.FirstOrDefault(r => r.GitHubRepositoryId == update.RepoId);
            if (repo != null)
            {
                repo.GitVersion = update.Info.Version == "-" ? null : update.Info.Version;
                repo.BranchName = update.Info.Branch == "-" ? null : update.Info.Branch;
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
                    await _gitCommandService.CloneAsync(workspacePath, repo.CloneUrl, cancellationToken);
                }
            }

            var version = "-";
            var branch = "-";
            if (Directory.Exists(repoPath))
            {
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
