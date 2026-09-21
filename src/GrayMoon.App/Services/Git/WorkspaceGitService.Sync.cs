using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Agent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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

        var configuredRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, configuredRoot, cancellationToken);
        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);

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

        // Batched into one set-based query (instead of one EF round-trip per repo) before the parallel block.
        if (_connectorHealthService != null)
        {
            await _connectorHealthService.EnsureConnectorsHealthyForRepositoriesAsync(
                repos.Select(r => r.RepositoryId), cancellationToken);
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
                    workspaceName = workspaceFolderName,
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

        await PersistVersionsAsync(workspaceId, contextId, results, persistDependencyLevel: !skipDependencyLevelPersistence, cancellationToken);

        await ApplyDefaultTipVersionsForMergedFeatureReposAsync(workspaceId, contextId, cancellationToken);

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
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, contextId, cancellationToken);

        _logger.LogDebug("Sync completed for workspace {WorkspaceName}", workspace.Name);
        return results.ToDictionary(r => r.RepositoryId, r => r.info);
    }

    /// <summary>Refreshes version for a single repo and persists. Returns (success, errorMessage) for caller to report and optionally stop workflow.</summary>
    public async Task<(bool Success, string? ErrorMessage)> SyncSingleRepositoryAsync(int repositoryId, int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
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

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var response = await _agentBridge.SendCommandAsync("RefreshRepositoryVersion", new { workspaceName = workspaceFolderName, repositoryName = repo.RepositoryName, repositoryId = repo.RepositoryId, workspaceRoot }, cancellationToken);
        if (!response.Success)
        {
            var err = response.Error ?? "Refresh version failed.";
            _logger.LogWarning("RefreshRepositoryVersion failed for repo {RepositoryId}: {Error}", repositoryId, err);
            return (false, err);
        }

        var info = ParseRefreshRepositoryVersionResponse(response);

        await PersistVersionsAsync(workspaceId, contextId, [(repo.RepositoryId, info)], true, cancellationToken);

        var allLinks = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.SyncStatus)
            .ToListAsync(cancellationToken);
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);
        await _workspaceRepository.UpdateSyncMetadataAsync(workspaceId, DateTime.UtcNow, isInSync);

        if (_fileVersionService != null)
            await _fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, contextId, cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, contextId.Value, cancellationToken);

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        return (true, null);
    }

    public async Task<IReadOnlyDictionary<int, RepoSyncStatus>> GetRepoSyncStatusAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        foreach (var wr in workspaceRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repo = wr.Repository;
            if (repo == null) continue;

            var response = await _agentBridge.SendCommandAsync("GetRepositoryVersion", new { workspaceName = workspaceFolderName, repositoryName = repo.RepositoryName, workspaceRoot }, cancellationToken);
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

    /// <summary>
    /// Closes out a user action: recomputes file-version and dependency stats for the given context, then
    /// broadcasts sync once so the grid refreshes. Call exactly once per action, after every
    /// repository in the batch has been written.
    /// </summary>
    public Task RecomputeAndBroadcastWorkspaceSyncedAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
        => _recomputeScope.CompleteAsync(workspaceId, contextId, cancellationToken);

    private async Task PersistVersionsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IEnumerable<(int RepoId, RepoGitVersionInfo info)> results,
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
    {
        var resultList = results.ToList();
        if (resultList.Count == 0) return;

        var repoIds = resultList.Select(r => r.RepoId).ToList();

        foreach (var (repoId, info) in resultList)
        {
            var snapshot = info.Snapshot ?? SnapshotFromFlatInfo(info);
            await _stateWriter.ApplyAsync(contextId, workspaceId, repoId, snapshot, new RepositoryStateWriteOptions
            {
                SyncStatus = SyncStatusWrite.Derive,
                ReconcilePullRequest = true,
            }, cancellationToken);
        }

        // The writer already merged each repository's projects; the dependency edges still have to be
        // merged as one batch so the level computation sees the whole graph at once.
        var syncResults = resultList.Select(r => (r.RepoId, r.info.ProjectsDetail)).ToList();
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, contextId.Value, persistDependencyLevel, cancellationToken);

        // Partial sync (single repo or whole level): merge uses persistDependencyLevel false so Persist is not
        // called with a partial uniqueEdges graph. Recompute from full ProjectDependencies in DB so every
        // WorkspaceRepositoryLink gets correct DependencyLevel/Dependencies/UnmatchedDeps without syncing other repos.
        if (!persistDependencyLevel)
            await RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);

        _logger.LogInformation("Persistence: saved WorkspaceRepository link versions. WorkspaceId={WorkspaceId}, RepoCount={RepoCount}",
            workspaceId, resultList.Count);
    }

    /// <summary>
    /// Fallback for <see cref="RepoGitVersionInfo"/> values built by code paths that do not yet produce a
    /// snapshot. Everything the flat shape carries is marked probed, which matches the merge behaviour those
    /// paths had before, and the groups it says nothing about stay untouched.
    /// </summary>
    private static RepositoryStateSnapshot SnapshotFromFlatInfo(RepoGitVersionInfo info)
    {
        var onTag = !string.IsNullOrWhiteSpace(info.Tag);
        return new RepositoryStateSnapshot
        {
            BranchName = info.Branch,
            CheckedOutTag = info.Tag,
            GitVersion = info.Version == "-" ? null : info.Version,
            DefaultBranchName = info.DefaultBranch,
            OutgoingCommits = info.OutgoingCommits,
            IncomingCommits = info.IncomingCommits,
            DefaultBranchBehind = info.DefaultBranchBehindCommits,
            DefaultBranchAhead = info.DefaultBranchAheadCommits,
            HasUpstream = info.HasUpstream,
            LocalBranches = info.LocalBranches?.ToList(),
            RemoteBranches = info.RemoteBranches?.ToList(),
            Tags = info.Tags?.ToList(),
            Projects = ToProjectNotifications(info.ProjectsDetail),
            ErrorMessage = info.ErrorMessage,
            IdentityProbed = true,
            GitVersionProbed = info.Version != "-",
            CommitCountsProbed = !onTag && (info.OutgoingCommits.HasValue || info.IncomingCommits.HasValue),
            UpstreamProbed = !onTag && info.HasUpstream.HasValue,
            BranchesProbed = info.LocalBranches != null || info.RemoteBranches != null || info.Tags != null,
            ProjectsProbed = info.ProjectsDetail != null,
        };
    }

    /// <summary>
    /// For Feature contexts: when a repo's Feature PR is merged, replace context GitVersion with the
    /// InformationalVersion of <c>origin/{default}</c> tip so Update Dependencies writes default-branch
    /// version strings into higher-level csprojs (no checkout of main required).
    /// </summary>
    private async Task ApplyDefaultTipVersionsForMergedFeatureReposAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        var info = await _contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        if (info.IsSpecialWorkspace)
            return;

        var mergedRepoWrlIds = await _dbContext.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == contextId.Value && p.MergedAt != null)
            .Select(p => p.WorkspaceRepositoryId)
            .ToListAsync(cancellationToken);
        if (mergedRepoWrlIds.Count == 0)
            return;

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => l.WorkspaceId == workspaceId && mergedRepoWrlIds.Contains(l.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return;

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspaceId, contextId, cancellationToken);
        foreach (var link in links)
        {
            var repoName = link.Repository?.RepositoryName;
            if (string.IsNullOrWhiteSpace(repoName))
                continue;

            var response = await _agentBridge.SendCommandAsync(
                "GetGitVersionAtDefaultTip",
                new { workspaceName = workspaceFolderName, repositoryName = repoName, workspaceRoot },
                cancellationToken);
            if (!response.Success || response.Data == null)
            {
                _logger.LogDebug(
                    "GetGitVersionAtDefaultTip failed for {Repo} in Feature context {ContextId}: {Error}",
                    repoName, contextId.Value, response.Error);
                continue;
            }

            var payload = AgentResponseJson.DeserializeAgentResponse<DefaultTipVersionAgentResponse>(response.Data);
            if (payload is not { Success: true } || string.IsNullOrWhiteSpace(payload.Version))
            {
                _logger.LogDebug(
                    "GetGitVersionAtDefaultTip returned no version for {Repo}: {Error}",
                    repoName, payload?.ErrorMessage);
                continue;
            }

            var state = await _dbContext.WorkspaceRepositoryContextStates
                .FirstOrDefaultAsync(
                    s => s.WorkspaceFeatureContextId == contextId.Value
                         && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId,
                    cancellationToken);
            if (state == null)
                continue;

            if (string.Equals(state.GitVersion, payload.Version, StringComparison.Ordinal))
                continue;

            state.GitVersion = payload.Version;
            _logger.LogInformation(
                "Feature context {ContextId}: set GitVersion for {Repo} from origin/{DefaultBranch} tip to {Version}",
                contextId.Value, repoName, payload.DefaultBranch, payload.Version);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed class DefaultTipVersionAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("defaultBranch")]
        public string? DefaultBranch { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }
}
