using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

public class WorkspaceGitService
{
    private readonly GitCommandService _gitCommandService;
    private readonly GitVersionCommandService _gitVersionCommandService;
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceRepository _workspaceRepository;
    private readonly ILogger<WorkspaceGitService> _logger;
    private readonly int _maxConcurrent;

    public WorkspaceGitService(
        GitCommandService gitCommandService,
        GitVersionCommandService gitVersionCommandService,
        WorkspaceService workspaceService,
        WorkspaceRepository workspaceRepository,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILogger<WorkspaceGitService> logger)
    {
        _gitCommandService = gitCommandService ?? throw new ArgumentNullException(nameof(gitCommandService));
        _gitVersionCommandService = gitVersionCommandService ?? throw new ArgumentNullException(nameof(gitVersionCommandService));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var max = workspaceOptions?.Value?.MaxConcurrentGitOperations ?? 8;
        _maxConcurrent = max < 1 ? 1 : max;
    }

    public async Task CloneAllAsync(int workspaceId, CancellationToken cancellationToken = default)
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
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.CloneUrl))
            .Cast<GitHubRepository>()
            .ToList();

        var toClone = repos
            .Where(repo => !Directory.Exists(Path.Combine(workspacePath, repo.RepositoryName)))
            .ToList();

        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var cloneTasks = toClone.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await _gitCommandService.CloneAsync(workspacePath, repo.CloneUrl!, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(cloneTasks);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetVersionsAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            return new Dictionary<int, string>();
        }

        var workspacePath = _workspaceService.GetWorkspacePath(workspace.Name);
        if (!Directory.Exists(workspacePath))
        {
            return new Dictionary<int, string>();
        }

        var repos = workspace.Repositories
            .Select(link => link.GitHubRepository)
            .Where(r => r != null)
            .Cast<GitHubRepository>()
            .ToList();

        var repoPaths = repos
            .Select(repo => (Repo: repo, Path: Path.Combine(workspacePath, repo.RepositoryName)))
            .Where(x => Directory.Exists(x.Path))
            .ToList();

        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var versionTasks = repoPaths.Select(async x =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var version = await _gitVersionCommandService.GetVersionAsync(x.Path, cancellationToken);
                return (x.Repo.GitHubRepositoryId, Version: version ?? "-");
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(versionTasks);
        return results.ToDictionary(r => r.GitHubRepositoryId, r => r.Version);
    }
}
