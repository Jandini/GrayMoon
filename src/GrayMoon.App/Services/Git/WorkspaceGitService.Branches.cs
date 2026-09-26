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
    /// <summary>Refreshes branches for a single repository by calling the agent directly. Routes CommandOutput to TerminalSinkContext when called within a background job.</summary>
    public async Task<bool> RefreshBranchesForRepositoryAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null) return false;

        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null) return false;

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return false;

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await _agentBridge.SendCommandAsync("RefreshBranches", new
        {
            workspaceName = workspace.Name,
            repositoryId = repo.RepositoryId,
            repositoryName = repo.RepositoryName,
            workspaceRoot
        }, cancellationToken);

        if (!response.Success) return false;

        var refreshResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(response.Data);
        if (refreshResponse == null) return false;

        var localBranches = refreshResponse.LocalBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var remoteBranches = refreshResponse.RemoteBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var tags = refreshResponse.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        await PersistBranchesAsync(wr.WorkspaceRepositoryId, localBranches, remoteBranches, refreshResponse.DefaultBranch, tags, refreshResponse.CurrentTag, cancellationToken);

        // BranchHasUpstream is only written from the agent's own git-config probe. Deriving it here by
        // matching the branch name against the remote list said "has upstream" for any branch that merely
        // shares a name with a remote ref, which is how a freshly checked-out default branch could end up
        // with the upstream badge instead of its commit counts.
        if (refreshResponse.UpstreamProbed && string.IsNullOrWhiteSpace(refreshResponse.CurrentTag))
        {
            wr.BranchHasUpstream = refreshResponse.HasUpstream;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task RefreshBranchesAndBroadcastAsync(int repositoryId, int workspaceId, CancellationToken cancellationToken = default)
    {
        await RefreshBranchesForRepositoryAsync(repositoryId, workspaceId, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken: cancellationToken);
    }

    /// <summary>Persists branches for a workspace repository. Removes branches not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master).</summary>
    public Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName = null,
        CancellationToken cancellationToken = default)
        => PersistBranchesAsync(workspaceRepositoryId, localBranches, remoteBranches, defaultBranchName, tags: null, currentTag: null, cancellationToken);

    /// <summary>Persists branches and tags for a workspace repository. Removes branches/tags not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master) and the currently checked-out tag.</summary>
    public Task PersistBranchesAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default)
        => _branchWriter.PersistAsync(workspaceRepositoryId, localBranches, remoteBranches, defaultBranchName, tags, currentTag, cancellationToken);

    /// <summary>Creates a new branch in all workspace repos (in parallel), then checks it out. baseBranch is "__default__" to use each repo's default, or a branch name. When <paramref name="repositoryIds"/> is set, only those repos are included. When <paramref name="syncState"/> is true, hooks are suppressed and the agent returns full state inline so the app can persist it without waiting for async hook syncs.</summary>
    public async Task<IReadOnlyDictionary<int, string>> CreateBranchesAsync(
        int workspaceId,
        string newBranchName,
        string baseBranch,
        Action<int, int>? onProgress = null,
        IReadOnlySet<int>? repositoryIds = null,
        bool syncState = false,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to create branches.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var links = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Include(wr => wr.Repository)
            .ToListAsync(cancellationToken);

        if (repositoryIds != null && repositoryIds.Count > 0)
            links = links.Where(wr => repositoryIds.Contains(wr.RepositoryId)).ToList();

        if (links.Count == 0)
            return new Dictionary<int, string>();

        var errors = new ConcurrentDictionary<int, string>();
        var useDefaultBase = string.Equals(baseBranch, "__default__", StringComparison.OrdinalIgnoreCase);
        var completedCount = 0;
        var totalCount = links.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        // Prefetch all default branches before the parallel section to avoid concurrent DbContext reads
        Dictionary<int, string>? defaultBranchByWrId = null;
        if (useDefaultBase)
        {
            var wrIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
            var defaultRows = await _dbContext.RepositoryBranches
                .Where(rb => wrIds.Contains(rb.WorkspaceRepositoryId) && rb.IsDefault)
                .Select(rb => new { rb.WorkspaceRepositoryId, rb.BranchName })
                .ToListAsync(cancellationToken);
            defaultBranchByWrId = new Dictionary<int, string>();
            foreach (var row in defaultRows)
                defaultBranchByWrId.TryAdd(row.WorkspaceRepositoryId, row.BranchName);
        }

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
                    baseBranchName = defaultBranchByWrId?.GetValueOrDefault(wr.WorkspaceRepositoryId) ?? "main";
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
                    baseBranchName,
                    workspaceRoot,
                    repositoryId = wr.RepositoryId,
                    skipHooks = syncState
                };
                var response = await _agentBridge.SendCommandAsync("CreateBranch", args, cancellationToken);
                var createResponse = AgentResponseJson.DeserializeAgentResponse<CreateBranchResponse>(response.Data);
                var success = createResponse?.Success ?? response.Success;

                if (success)
                {
                    wr.BranchName = createResponse?.Branch ?? newBranchName;
                    if (syncState && createResponse != null)
                    {
                        // Hooks were suppressed - persist all state returned inline so the next
                        // step (dependency update) sees a complete, consistent database.
                        wr.CheckedOutTag = null;
                        if (createResponse.Version != null)
                            wr.GitVersion = createResponse.Version;
                        if (createResponse.OutgoingCommits.HasValue)
                            wr.OutgoingCommits = createResponse.OutgoingCommits;
                        if (createResponse.IncomingCommits.HasValue)
                            wr.IncomingCommits = createResponse.IncomingCommits;
                        if (createResponse.HasUpstream.HasValue)
                            wr.BranchHasUpstream = createResponse.HasUpstream;
                        if (createResponse.DefaultBranchBehind.HasValue)
                            wr.DefaultBranchBehindCommits = createResponse.DefaultBranchBehind;
                        if (createResponse.DefaultBranchAhead.HasValue)
                            wr.DefaultBranchAheadCommits = createResponse.DefaultBranchAhead;
                    }
                }
                else
                {
                    var err = createResponse?.ErrorMessage ?? response.Error ?? "Failed to create branch.";
                    errors[wr.RepositoryId] = err;
                }
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

        // Persist the new branch for each repo where creation succeeded (so it appears in branch lists without a manual refresh)
        foreach (var wr in links.Where(wr => wr.BranchName == newBranchName))
        {
            await EnsureLocalBranchPersistedAsync(wr.WorkspaceRepositoryId, newBranchName, cancellationToken);
        }

        _hubContext?.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
        return errors;
    }

    /// <summary>Ensures a local branch is present in RepositoryBranches for the given workspace repository. Adds it if missing; does not remove other branches.</summary>
    public async Task EnsureLocalBranchPersistedAsync(int workspaceRepositoryId, string branchName, CancellationToken cancellationToken = default)
    {
        var exists = await _dbContext.RepositoryBranches
            .AnyAsync(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId && rb.BranchName == branchName && !rb.IsRemote, cancellationToken);
        if (exists)
            return;
        _dbContext.RepositoryBranches.Add(new RepositoryBranch
        {
            WorkspaceRepositoryId = workspaceRepositoryId,
            BranchName = branchName,
            IsRemote = false,
            LastSeenAt = DateTime.UtcNow,
            IsDefault = false
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Adds the given branch as a remote branch to persistence and sets BranchHasUpstream on the workspace repository link. Used after a successful push so the branch appears in Remotes without calling refresh branches.</summary>
    public async Task EnsureRemoteBranchPersistedAsync(int workspaceId, int repositoryId, string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return;
        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return;
        var remoteBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? branchName : "origin/" + branchName;
        var exists = await _dbContext.RepositoryBranches
            .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote && rb.BranchName == remoteBranchName, cancellationToken);
        if (!exists)
        {
            _dbContext.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                BranchName = remoteBranchName,
                IsRemote = true,
                LastSeenAt = DateTime.UtcNow,
                IsDefault = false
            });
        }
        wr.BranchHasUpstream = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
