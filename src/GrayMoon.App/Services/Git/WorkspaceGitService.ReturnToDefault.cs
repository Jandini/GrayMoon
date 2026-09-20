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
    /// <summary>Runs GetCommitCounts (agent) for each repo and returns DefaultBranchAhead and HasUpstream per repo. Used to check if return-to-default is safe (no commits ahead of default). Respects MaxParallelOperations.</summary>
    public async Task<IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>> GetCommitCountsForReposAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string RepoName)> repos,
        CancellationToken cancellationToken = default)
    {
        if (repos.Count == 0)
            return Array.Empty<(int, int?, bool?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Array.Empty<(int, int?, bool?)>();

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, cancellationToken);
        var maxParallel = _maxConcurrent;

        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = repos.Select(async tuple =>
        {
            var (repoId, repoName) = tuple;
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    var response = await _agentBridge.SendCommandAsync("GetCommitCounts", new
                    {
                        workspaceName = workspaceFolderName,
                        repositoryName = repoName,
                        workspaceRoot
                    }, cancellationToken);
                    if (!response.Success || response.Data == null)
                        return (RepoId: repoId, DefaultAhead: (int?)null, HasUpstream: (bool?)null);
                    var data = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(response.Data);
                    return (RepoId: repoId, DefaultAhead: data?.DefaultBranchAhead, HasUpstream: data?.HasUpstream);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetCommitCounts failed for repo {RepoId} ({RepoName})", repoId, repoName);
                    return (RepoId: repoId, DefaultAhead: (int?)null, HasUpstream: (bool?)null);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Syncs a single repository to its default branch by calling the agent directly, so CommandOutput flows to TerminalSinkContext when called inside a background job.
    /// Persists the resulting state through <see cref="WorkspaceRepositoryStateWriter"/> but does not recompute workspace-wide stats or broadcast:
    /// the caller owns that boundary and must call <see cref="RecomputeAndBroadcastWorkspaceSyncedAsync"/> once after its whole batch, single-repository batches included.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> ReturnToDefaultDirectAsync(
        int workspaceId,
        int repositoryId,
        string currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, "Workspace not found.");

        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
            return (false, "Repository not found.");

        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return (false, "Repository is not in the given workspace.");

        if (_connectorHealthService != null)
            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

        await _workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, cancellationToken);

        var wrWithPr = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(x => x.PullRequest)
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        var prInfo = wrWithPr?.PullRequest?.PullRequestNumber.HasValue == true
            ? wrWithPr.PullRequest.ToPullRequestInfo()
            : null;
        // "Delete local branches" is the user's own confirmation, given on a dialog that lists how many
        // commits each repository would lose and only enables Proceed after a countdown. Requiring a merged
        // or closed pull request on top of it made git fall back to "git branch -d", which refuses to delete
        // exactly the unmerged branches the dialog just promised to remove, so the branch survived the sync.
        // A merged or closed pull request stays an independent reason the branch is safe to drop.
        var forceDeleteLocalBranch = allowForceDeleteLocalBranch || prInfo?.IsMerged == true || prInfo?.IsClosed == true;

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, cancellationToken);
        var args = new
        {
            workspaceName = workspaceFolderName,
            repositoryName = repo.RepositoryName,
            currentBranchName,
            bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
            workspaceRoot,
            forceDeleteLocalBranch,
            deleteRemoteBranch
        };

        var response = await _agentBridge.SendCommandAsync("ReturnToDefaultBranch", args, cancellationToken);
        var syncResponse = AgentResponseJson.DeserializeAgentResponse<ReturnToDefaultBranchResponse>(response.Data);
        var commandSuccess = syncResponse?.Success ?? response.Success;
        var errorMessage = syncResponse?.ErrorMessage ?? response.Error ?? "Failed to return to default branch";

        if (!commandSuccess)
            return (false, errorMessage);

        if (syncResponse?.LocalBranches == null)
        {
            // The agent reported no branch lists, so the writer cannot replace them. Remove at least the
            // branch that was just deleted locally.
            var toRemove = await _dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote && rb.BranchName == currentBranchName)
                .ToListAsync(cancellationToken);
            if (toRemove.Count > 0)
            {
                _dbContext.RepositoryBranches.RemoveRange(toRemove);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // One authoritative write of branch, version, counts, upstream, branch rows, projects and the PR
        // row, so no field of the previous branch survives the switch to the default branch.
        var snapshot = BuildReturnToDefaultSnapshot(syncResponse);
        await _stateWriter.ApplyAsync(workspaceId, repositoryId, snapshot, new RepositoryStateWriteOptions
        {
            SyncStatus = SyncStatusWrite.Derive,
            ReconcilePullRequest = true,
        }, cancellationToken);

        return (true, null);
    }

    /// <summary>
    /// Builds the state snapshot for a return-to-default response. Newer agents send an explicit snapshot
    /// with probe markers; older ones send the flat fields, which are mapped here with the markers a
    /// successful return-to-default is known to satisfy.
    /// </summary>
    private static RepositoryStateSnapshot BuildReturnToDefaultSnapshot(ReturnToDefaultBranchResponse? syncResponse)
    {
        if (syncResponse == null)
            return new RepositoryStateSnapshot();

        if (syncResponse.State != null)
            return syncResponse.State;

        var branchesProbed = syncResponse.LocalBranches != null;
        return new RepositoryStateSnapshot
        {
            BranchName = syncResponse.CurrentBranch ?? syncResponse.DefaultBranch,
            CheckedOutTag = syncResponse.CurrentTag,
            GitVersion = syncResponse.GitVersion,
            DefaultBranchName = syncResponse.DefaultBranch,
            OutgoingCommits = syncResponse.OutgoingCommits,
            IncomingCommits = syncResponse.IncomingCommits,
            DefaultBranchBehind = syncResponse.DefaultBranchBehind,
            DefaultBranchAhead = syncResponse.DefaultBranchAhead,
            HasUpstream = syncResponse.HasUpstream,
            LocalBranches = syncResponse.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList(),
            RemoteBranches = syncResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? (branchesProbed ? [] : null),
            Tags = syncResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? (branchesProbed ? [] : null),
            Projects = syncResponse.Projects != null ? ToProjectNotifications(GetProjectsDetail(syncResponse.Projects)) ?? [] : null,
            IdentityProbed = true,
            GitVersionProbed = !string.IsNullOrWhiteSpace(syncResponse.GitVersion),
            // A pre-snapshot agent only reaches this point after a successful checkout and pull, at which
            // point it always ran both count queries and the upstream check.
            CommitCountsProbed = true,
            UpstreamProbed = syncResponse.HasUpstream.HasValue,
            BranchesProbed = branchesProbed,
            ProjectsProbed = syncResponse.Projects != null,
        };
    }
}
