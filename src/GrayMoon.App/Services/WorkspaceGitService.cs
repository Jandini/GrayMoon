using System.Reflection;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

public class WorkspaceGitService
{
    private readonly GitCommandService _gitCommandService;
    private readonly GitVersionCommandService _gitVersionCommandService;
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceRepository _workspaceRepository;
    private readonly GitHubRepositoryRepository _githubRepositoryRepository;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<WorkspaceGitService> _logger;
    private readonly int _maxConcurrent;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly IServer? _server;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext;

    public WorkspaceGitService(
        GitCommandService gitCommandService,
        GitVersionCommandService gitVersionCommandService,
        WorkspaceService workspaceService,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository githubRepositoryRepository,
        AppDbContext dbContext,
        IOptions<WorkspaceOptions> workspaceOptions,
        IConfiguration configuration,
        ILogger<WorkspaceGitService> logger,
        IServer? server = null,
        IHubContext<WorkspaceSyncHub>? hubContext = null)
    {
        _gitCommandService = gitCommandService ?? throw new ArgumentNullException(nameof(gitCommandService));
        _gitVersionCommandService = gitVersionCommandService ?? throw new ArgumentNullException(nameof(gitVersionCommandService));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _githubRepositoryRepository = githubRepositoryRepository ?? throw new ArgumentNullException(nameof(githubRepositoryRepository));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _server = server;
        _hubContext = hubContext;
        _workspaceOptions = workspaceOptions?.Value ?? new WorkspaceOptions();
        var max = _workspaceOptions.MaxConcurrentGitOperations;
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
            var result = await RunSyncJobAsync(workspaceId, repo, workspacePath, semaphore, cancellationToken);
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

    /// <summary>
    /// Syncs a single repository by its id in the given workspace only.
    /// Call from a background task (e.g. after returning 202 from API); uses scoped services.
    /// Returns false if repo not found or repo is not in the workspace; true if sync was run.
    /// </summary>
    public async Task<bool> SyncSingleRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _githubRepositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
        {
            _logger.LogWarning("Sync skipped: repository not found for id {RepositoryId}", repositoryId);
            return false;
        }

        var isInWorkspace = await _dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.GitHubRepositoryId == repo.GitHubRepositoryId, cancellationToken);
        if (!isInWorkspace)
        {
            _logger.LogWarning("Sync skipped: repository {RepositoryName} (id {RepositoryId}) is not in workspace {WorkspaceId}", repo.RepositoryName, repositoryId, workspaceId);
            return false;
        }

        _logger.LogInformation("Syncing repository {RepositoryName} (id {RepositoryId}) in workspace {WorkspaceId}", repo.RepositoryName, repositoryId, workspaceId);
        await SyncSingleRepositoryInWorkspaceAsync(workspaceId, repo, cancellationToken);
        return true;
    }

    /// <summary>
    /// Syncs one repository in a single workspace (clone if needed, get version, persist).
    /// </summary>
    public async Task SyncSingleRepositoryInWorkspaceAsync(int workspaceId, GitHubRepository repo, CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            _logger.LogWarning("Workspace {WorkspaceId} not found for single-repo sync", workspaceId);
            return;
        }

        var workspacePath = _workspaceService.GetWorkspacePath(workspace.Name);
        _workspaceService.CreateDirectory(workspace.Name);

        using var semaphore = new SemaphoreSlim(1);
        var (repoId, info) = await RunSyncJobAsync(workspaceId, repo, workspacePath, semaphore, cancellationToken);
        await PersistVersionsAsync(workspaceId, new[] { (repoId, info) }, cancellationToken);

        var statuses = await GetRepoSyncStatusAsync(workspaceId, cancellationToken: cancellationToken);
        var isInSync = statuses.Values.All(v => v == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        _logger.LogDebug("Single-repo sync completed for {RepositoryName} in workspace {WorkspaceName}", repo.RepositoryName, workspace.Name);
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
        var resultList = results.ToList();
        if (resultList.Count == 0)
        {
            return;
        }

        var repoIds = resultList.Select(r => r.RepoId).ToList();
        var workspaceReposToUpdate = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.GitHubRepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var result in resultList)
        {
            var wr = workspaceReposToUpdate.FirstOrDefault(wr => wr.GitHubRepositoryId == result.RepoId);
            if (wr != null)
            {
                wr.GitVersion = result.Info.Version == "-" ? null : result.Info.Version;
                wr.BranchName = result.Info.Branch == "-" ? null : result.Info.Branch;
                wr.SyncStatus = (result.Info.Version == "-" || result.Info.Branch == "-")
                    ? RepoSyncStatus.Error
                    : RepoSyncStatus.InSync;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int RepoId, RepoGitVersionInfo Info)> RunSyncJobAsync(
        int workspaceId,
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
                    await _gitCommandService.CloneAsync(workspacePath, repo.CloneUrl, repo.GitHubConnector?.UserToken, cancellationToken);
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

                    if (version != "-" && branch != "-")
                        WriteSyncHooks(repoPath, workspaceId, repo.GitHubRepositoryId);
                }
            }

            return (repo.GitHubRepositoryId, new RepoGitVersionInfo { Version = version, Branch = branch });
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void WriteSyncHooks(string repoPath, int workflowId, int repoId)
    {
        var baseUrl = GetPostCommitHookBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogDebug("Skipping sync hooks: no base URL (set Workspace:PostCommitHookBaseUrl or ensure urls/ASPNETCORE_URLS is set)");
            return;
        }
        baseUrl = baseUrl.TrimEnd('/');
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        var comment = $"# Created by GrayMoon {version} at {now}.\n";
        var curlLine = $"curl -s -X POST \"{baseUrl}/api/sync\" -H \"Content-Type: application/json\" -d '{{\"repositoryId\":{repoId},\"workspaceId\":{workflowId}}}'\n";
        var hooksDir = Path.Combine(repoPath, ".git", "hooks");
        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            Directory.CreateDirectory(hooksDir);

            // post-commit: run after every commit
            var postCommit = "#!/bin/sh\n" + comment + curlLine;
            WriteHookFile(Path.Combine(hooksDir, "post-commit"), postCommit, utf8NoBom);

            // post-checkout: run after checkout; $3=1 means branch checkout (not file checkout)
            var postCheckout = "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] && " + curlLine.TrimEnd() + "\n";
            WriteHookFile(Path.Combine(hooksDir, "post-checkout"), postCheckout, utf8NoBom);

            _logger.LogDebug("Sync hooks (post-commit, post-checkout) written for repo {RepoId} in workspace {WorkflowId}", repoId, workflowId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write sync hooks in {HooksDir}", hooksDir);
        }
    }

    private static void WriteHookFile(string hookPath, string content, System.Text.Encoding encoding)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(hookPath, normalized, encoding);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string? GetPostCommitHookBaseUrl()
    {
        var configured = _workspaceOptions.PostCommitHookBaseUrl?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return configured;

        // Port-only: use 127.0.0.1 with configured port (app is designed for localhost)
        if (_workspaceOptions.PostCommitHookPort is int port and > 0)
            return BuildLocalHostHookUrl("http", port);

        var fromServer = GetBaseUrlFromServer();
        if (!string.IsNullOrEmpty(fromServer))
            return fromServer;

        var fromConfig = _configuration["urls"] ?? _configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrEmpty(fromConfig))
        {
            var first = fromConfig.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(first))
                return NormalizeListenUrlForHook(first);
        }

        return null;
    }

    private string? GetBaseUrlFromServer()
    {
        if (_server?.Features == null)
            return null;
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var first = addresses?.FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : NormalizeListenUrlForHook(first);
    }

    /// <summary>Normalizes a listen URL (e.g. http://[::]:8384 or http://+:8384) to a localhost hook URL. Only the port is used; host is always 127.0.0.1 since the app is designed for local use.</summary>
    private static string NormalizeListenUrlForHook(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            // Try with default scheme (e.g. "[::]:8384" or "8384")
            if (Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out uri))
                return BuildLocalHostHookUrl(uri.Scheme, uri.Port);
            return trimmed;
        }
        return BuildLocalHostHookUrl(uri.Scheme, uri.Port);
    }

    private static string BuildLocalHostHookUrl(string scheme, int port)
    {
        var builder = new UriBuilder(scheme, "127.0.0.1", port);
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
}
