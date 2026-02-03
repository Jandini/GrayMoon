using System.Text.Json;
using GrayMoon.Agent.Hub;
using Microsoft.AspNetCore.SignalR.Client;
using GrayMoon.Agent.Models;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

public sealed class JobBackgroundService : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly GitOperations _git;
    private readonly IHubConnectionProvider _hubProvider;
    private readonly ILogger<JobBackgroundService> _logger;
    private readonly int _maxConcurrent;

    public JobBackgroundService(
        IJobQueue jobQueue,
        GitOperations git,
        IHubConnectionProvider hubProvider,
        IOptions<AgentOptions> options,
        ILogger<JobBackgroundService> logger)
    {
        _jobQueue = jobQueue;
        _git = git;
        _hubProvider = hubProvider;
        _logger = logger;
        _maxConcurrent = Math.Max(1, options.Value.MaxConcurrentCommands);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobBackgroundService starting with {MaxConcurrent} workers", _maxConcurrent);
        var workers = Enumerable.Range(0, _maxConcurrent)
            .Select(i => ProcessAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task ProcessAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var job in _jobQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (job.IsNotify)
                    await ProcessNotifyAsync(job, stoppingToken);
                else
                    await ProcessCommandAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} failed processing job {Command}", workerId, job.Command);
                if (!job.IsNotify && job.RequestId != null)
                    await SendResponseAsync(job.RequestId, false, null, ex.Message);
            }
        }
    }

    private async Task ProcessNotifyAsync(QueuedJob job, CancellationToken ct)
    {
        if (job.RepositoryId == null || job.WorkspaceId == null || string.IsNullOrWhiteSpace(job.RepositoryPath))
        {
            _logger.LogWarning("NotifySync job missing repositoryId, workspaceId, or repositoryPath");
            return;
        }

        // No AddSafeDirectory — repo was cloned by SyncRepository which calls it only after CloneAsync
        var versionResult = await _git.GetVersionAsync(job.RepositoryPath, ct);
        var version = versionResult?.SemVer ?? versionResult?.FullSemVer ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var connection = _hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("SyncCommand", job.WorkspaceId.Value, job.RepositoryId.Value, version, branch, ct);
            _logger.LogInformation("SyncCommand sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}",
                job.WorkspaceId, job.RepositoryId, version, branch);
        }
        else
        {
            _logger.LogWarning("Hub not connected, cannot send SyncCommand");
        }
    }

    private async Task ProcessCommandAsync(QueuedJob job, CancellationToken ct)
    {
        var args = job.Args ?? default;
        object? data = job.Command switch
        {
            "SyncRepository" => await HandleSyncRepositoryAsync(args, ct),
            "RefreshRepositoryVersion" => await HandleRefreshRepositoryVersionAsync(args, ct),
            "EnsureWorkspace" => await HandleEnsureWorkspaceAsync(args, ct),
            "GetWorkspaceRepositories" => await HandleGetWorkspaceRepositoriesAsync(args, ct),
            "GetRepositoryVersion" => await HandleGetRepositoryVersionAsync(args, ct),
            "GetWorkspaceExists" => await HandleGetWorkspaceExistsAsync(args, ct),
            _ => throw new NotSupportedException($"Unknown command: {job.Command}")
        };

        await SendResponseAsync(job.RequestId!, true, data, null);
    }

    private async Task<object> HandleSyncRepositoryAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for SyncRepository");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var repositoryId = GetInt(args, "repositoryId") ?? throw new ArgumentException("repositoryId required");
        var repositoryName = GetString(args, "repositoryName") ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = GetString(args, "cloneUrl");
        var bearerToken = GetString(args, "bearerToken");
        var workspaceId = GetInt(args, "workspaceId") ?? throw new ArgumentException("workspaceId required");

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var wasCloned = false;

        _git.CreateDirectory(workspacePath);

        if (!_git.DirectoryExists(repoPath) && !string.IsNullOrWhiteSpace(cloneUrl))
        {
            var ok = await _git.CloneAsync(workspacePath, cloneUrl, bearerToken, ct);
            wasCloned = ok;
            if (ok)
                await _git.AddSafeDirectoryAsync(repoPath, ct); // only and only after CloneAsync
        }

        var version = "-";
        var branch = "-";
        if (_git.DirectoryExists(repoPath))
        {
            var vr = await _git.GetVersionAsync(repoPath, ct);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
                if (version != "-" && branch != "-")
                    _git.WriteSyncHooks(repoPath, workspaceId, repositoryId);
            }
        }

        return new { version, branch, wasCloned };
    }

    private async Task<object> HandleRefreshRepositoryVersionAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for RefreshRepositoryVersion");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var repositoryName = GetString(args, "repositoryName") ?? throw new ArgumentException("repositoryName required");

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        var version = "-";
        var branch = "-";
        if (_git.DirectoryExists(repoPath))
        {
            var vr = await _git.GetVersionAsync(repoPath, ct);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }
        }

        return new { version, branch };
    }

    private Task<object> HandleEnsureWorkspaceAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for EnsureWorkspace");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        _git.CreateDirectory(path);
        return Task.FromResult<object>(new { });
    }

    private Task<object> HandleGetWorkspaceRepositoriesAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for GetWorkspaceRepositories");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var repositories = _git.GetDirectories(path);
        return Task.FromResult<object>(new { repositories });
    }

    private async Task<object> HandleGetRepositoryVersionAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for GetRepositoryVersion");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var repositoryName = GetString(args, "repositoryName") ?? throw new ArgumentException("repositoryName required");

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var exists = _git.DirectoryExists(repoPath);

        string? version = null;
        string? branch = null;
        if (exists)
        {
            var vr = await _git.GetVersionAsync(repoPath, ct);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer;
                branch = vr.BranchName ?? vr.EscapedBranchName;
            }
        }

        return new { exists, version, branch };
    }

    private Task<object> HandleGetWorkspaceExistsAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Args required for GetWorkspaceExists");

        var workspaceName = GetString(args, "workspaceName") ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var exists = _git.DirectoryExists(path);
        return Task.FromResult<object>(new { exists });
    }

    private async Task SendResponseAsync(string requestId, bool success, object? data, string? error)
    {
        var connection = _hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
            await connection.InvokeAsync("ResponseCommand", requestId, success, data, error);
        else
            _logger.LogWarning("Hub not connected, cannot send ResponseCommand for {RequestId}", requestId);
    }

    private static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static int? GetInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var p) && p.TryGetInt32(out var i) ? i : null;
}
