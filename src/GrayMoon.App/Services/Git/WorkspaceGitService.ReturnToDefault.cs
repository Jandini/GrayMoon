using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>Runs GetCommitCounts (worker) for each repo and returns DefaultBranchAhead and HasUpstream per repo. Used to check if return-to-default is safe (no commits ahead of default). Respects MaxParallelOperations.</summary>
    public async Task<IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>> GetCommitCountsForReposAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<(int RepoId, string RepoName)> repos,
        CancellationToken cancellationToken = default)
    {
        if (repos.Count == 0)
            return Array.Empty<(int, int?, bool?)>();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Array.Empty<(int, int?, bool?)>();

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
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
                    var response = await _workerBridge.SendCommandAsync("GetCommitCounts", new
                    {
                        workspaceName = workspaceFolderName,
                        repositoryName = repoName,
                        workspaceRoot,
                        workspaceRepositoryName
                    }, cancellationToken);
                    if (!response.Success || response.Data == null)
                        return (RepoId: repoId, DefaultAhead: (int?)null, HasUpstream: (bool?)null);
                    var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerCommitCountsResponse>(response.Data);
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
    /// Syncs a single repository to its default branch by calling the worker directly, so CommandOutput flows to TerminalSinkContext when called inside a background job.
    /// Persists the resulting state through <see cref="WorkspaceRepositoryStateWriter"/> but does not recompute workspace-wide stats or broadcast:
    /// the caller owns that boundary and must call <see cref="RecomputeAndBroadcastWorkspaceSyncedAsync"/> once after its whole batch, single-repository batches included.
    /// A branch delete that failed does not fail the return: it comes back as <see cref="ReturnToDefaultOutcome.Warning"/>, which the caller must report.
    /// </summary>
    public async Task<ReturnToDefaultOutcome> ReturnToDefaultDirectAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new ReturnToDefaultOutcome(false, "Workspace not found.");

        var repo = await _repositoryRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
            return new ReturnToDefaultOutcome(false, "Repository not found.");

        var wr = await _dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return new ReturnToDefaultOutcome(false, "Repository is not in the given workspace.");

        if (_connectorHealthService != null)
            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

        await _workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, cancellationToken);

        var wrWithPr = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(x => x.PullRequest)
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        // Composed over the cloned repository and (for a fork) its upstream: a pull request merged or closed in either one makes the branch safe to drop.
        var prInfo = wrWithPr?.PullRequest?.ToPullRequestInfo();
        // "Delete local branches" is the user's own confirmation, given on a dialog that lists how many
        // commits each repository would lose and only enables Proceed after a countdown. Requiring a merged
        // or closed pull request on top of it made git fall back to "git branch -d", which refuses to delete
        // exactly the unmerged branches the dialog just promised to remove, so the branch survived the sync.
        // A merged or closed pull request stays an independent reason the branch is safe to drop.
        var forceDeleteLocalBranch = allowForceDeleteLocalBranch || prInfo?.IsMerged == true || prInfo?.IsClosed == true;

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        var capabilities = await ResolveRepositoryOperationCapabilitiesAsync(workspaceId, cancellationToken);
        var args = new
        {
            workspaceId,
            workspaceName = workspaceFolderName,
            repositoryName = repo.RepositoryName,
            currentBranchName,
            bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
            workspaceRoot,
            workspaceRepositoryName,
            forceDeleteLocalBranch,
            deleteRemoteBranch,
            capabilities
        };

        // Read before the command is sent: a hook snapshot the App received before this moment was captured on the
        // branch this return leaves, and must not overwrite what the return writes (it waited in the sync queue).
        var startedAt = RepositoryStateFreshness.Now();
        var response = await _workerBridge.SendCommandAsync("ReturnToDefaultBranch", args, cancellationToken);
        var syncResponse = WorkerResponseJson.DeserializeWorkerResponse<ReturnToDefaultBranchResponse>(response.Data);
        var commandSuccess = syncResponse?.Success ?? response.Success;
        var errorMessage = syncResponse?.ErrorMessage ?? response.Error ?? "Failed to return to default branch";

        if (!commandSuccess)
        {
            // A return that failed at the pull has already checked out the default branch, with hooks off. The
            // Worker then reports where the repository is; stored here, or the row keeps the branch and commit it left.
            if (syncResponse?.State is { } stateAfterFailure)
            {
                await _stateWriter.ApplyAsync(contextId, workspaceId, repositoryId, stateAfterFailure, new RepositoryStateWriteOptions
                {
                    SyncStatus = SyncStatusWrite.Derive,
                    ReconcilePullRequest = true,
                    OperationStartedAt = startedAt,
                }, cancellationToken);
            }

            return new ReturnToDefaultOutcome(false, errorMessage);
        }

        // App and Worker are version-locked: a successful return always carries the state it left behind.
        if (syncResponse?.State is not { } snapshot)
            return new ReturnToDefaultOutcome(false, "The Worker reported no repository state after returning to the default branch. Sync the repository.");

        // One authoritative write of branch, commit, version, counts, upstream, branch rows, projects and the PR
        // row, so no field of the previous branch survives the switch to the default branch.
        await _stateWriter.ApplyAsync(contextId, workspaceId, repositoryId, snapshot, new RepositoryStateWriteOptions
        {
            SyncStatus = SyncStatusWrite.Derive,
            ReconcilePullRequest = true,
            OperationStartedAt = startedAt,
        }, cancellationToken);

        return new ReturnToDefaultOutcome(true, null, ReturnToDefaultOutcome.DescribeBranchDeleteFailure(
            repo.RepositoryName, currentBranchName, syncResponse.RemoteBranchDeleteError, syncResponse.LocalBranchDeleteError));
    }
}

/// <summary>
/// Outcome of one repository's Return to Default. <see cref="Warning"/> is set on a success whose branch delete (remote,
/// local or both) failed: the repository is on the default branch, and the branch the user asked to delete is still there.
/// </summary>
public readonly record struct ReturnToDefaultOutcome(bool Success, string? ErrorMessage, string? Warning = null)
{
    public void Deconstruct(out bool success, out string? errorMessage)
    {
        success = Success;
        errorMessage = ErrorMessage;
    }

    /// <summary>One sentence naming the repository, the branch and what was not deleted. Null when both deletes worked.</summary>
    public static string? DescribeBranchDeleteFailure(string? repositoryName, string branchName, string? remoteError, string? localError)
    {
        var remoteFailed = !string.IsNullOrWhiteSpace(remoteError);
        var localFailed = !string.IsNullOrWhiteSpace(localError);
        if (!remoteFailed && !localFailed)
            return null;

        var name = string.IsNullOrWhiteSpace(repositoryName) ? "Repository" : repositoryName;
        if (remoteFailed && localFailed)
            return $"{name}: returned to the default branch, but the remote and the local branch '{branchName}' were not deleted (remote: {Trim(remoteError)}; local: {Trim(localError)}).";

        return remoteFailed
            ? $"{name}: returned to the default branch, but the remote branch '{branchName}' was not deleted ({Trim(remoteError)})."
            : $"{name}: returned to the default branch, but the local branch '{branchName}' was not deleted ({Trim(localError)}).";

        static string Trim(string? error) => error!.Trim().TrimEnd('.');
    }
}
