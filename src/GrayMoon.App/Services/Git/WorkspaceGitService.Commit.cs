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

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>Stages updated .csproj paths and commits with message "chore(deps): update package versions" plus the full list of packages (one line per package: "- {packageId} to {version}"). Runs up to 8 commits in parallel.</summary>
    public async Task<IReadOnlyList<(int RepoId, bool Committed, string? ErrorMessage)>> CommitDependencyUpdatesAsync(
        int workspaceId,
        IReadOnlyList<SyncDependenciesRepoPayload> reposToCommit,
        Action<int, int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? commitMessageOverride = null,
        bool includeDepsInCommitMessage = true,
        bool skipHooks = false)
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
                    workspaceRoot,
                    skipHooks
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
        string? commitMessageOverride = null,
        bool skipHooks = false)
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
                    workspaceRoot,
                    skipHooks
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
}
