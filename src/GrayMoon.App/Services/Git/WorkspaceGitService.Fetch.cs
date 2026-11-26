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
    /// <summary>
    /// The remote-only observation: git fetch (pruning, with tags) for every repo in the workspace, then what the
    /// fetch can change - commit counts and the upstream flag, the tag rows and the local and remote branch rows.
    /// No GitVersion, no csproj scan, no file-version check, no dependency recompute, and the checked-out branch
    /// and commit are not read again. Significantly faster than a full Sync. Pass <paramref name="repositoryIds"/> to restrict the fetch to
    /// a specific subset (e.g. one dependency level); null fetches every repository in the workspace.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, string>> QuickFetchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int>? repositoryIds = null,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new WorkerNotConnectedException();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;

        var links = workspace.Repositories
            .Where(l => l.Repository != null && (repositoryIds == null || repositoryIds.Contains(l.RepositoryId)))
            .ToList();

        if (links.Count == 0)
            return new Dictionary<int, string>();

        _logger.LogInformation("Quick Fetch triggered. Workspace={WorkspaceName}, RepoCount={Count}", workspace.Name, links.Count);

        var divergenceByRepoId = await GetDivergenceBaseBranchesByRepositoryIdAsync(contextId, cancellationToken);
        var capabilities = await ResolveRepositoryOperationCapabilitiesAsync(workspaceId, cancellationToken);

        var completedCount = 0;
        var totalCount = links.Count;
        // Before every FetchCommits command, so it is a safe "began at" for all of them.
        var startedAt = RepositoryStateFreshness.Now();
        using var semaphore = new SemaphoreSlim(_maxConcurrent);

        var fetchTasks = links.Select(async link =>
        {
            var repo = link.Repository!;
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                divergenceByRepoId.TryGetValue(repo.RepositoryId, out var divergenceBaseBranch);
                var args = new
                {
                    workspaceName = workspaceFolderName,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot,
                    workspaceRepositoryName,
                    divergenceBaseBranch,
                    capabilities
                };
                var response = await _workerBridge.SendCommandAsync("FetchCommits", args, cancellationToken);
                var data = response.Data != null
                    ? WorkerResponseJson.DeserializeWorkerResponse<WorkerFetchCommitsResponse>(response.Data)
                    : null;
                string? error = null;
                if (!response.Success || data is { Success: false, ErrorMessage: not null })
                    error = data?.ErrorMessage ?? response.Error ?? "Fetch failed.";
                var count = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(count, totalCount);
                return (link.WorkspaceRepositoryId, repo.RepositoryId, data, error);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(fetchTasks);

        // Persist what the fetch can change into the selected context (Feature or Workspace) through the state
        // writer: the commit counts and the upstream flag, and - when the Worker listed them - the tag and branch
        // rows, which prunes the remote branches the fetch pruned. Nothing here says what is checked out, so the
        // identity, the version and the projects are left alone and the sync status is not derived.
        foreach (var (wrId, repoId, data, error) in results)
        {
            if (data == null || !string.IsNullOrWhiteSpace(error)) continue;

            var onTag = !string.IsNullOrWhiteSpace(data.CurrentTag);
            await _stateWriter.ApplyAsync(contextId, workspaceId, repoId, new RepositoryStateSnapshot
            {
                OutgoingCommits = data.OutgoingCommits,
                IncomingCommits = data.IncomingCommits,
                DefaultBranchBehind = data.DefaultBranchBehind,
                DefaultBranchAhead = data.DefaultBranchAhead,
                HasUpstream = onTag ? null : data.HasUpstream,
                CommitCountsProbed = !onTag && data.CommitCountsProbed,
                UpstreamProbed = !onTag && data.HasUpstream.HasValue,
                CheckedOutTag = data.CurrentTag,
                DefaultBranchName = data.BranchesProbed ? data.DefaultBranch : null,
                LocalBranches = data.BranchesProbed ? data.LocalBranches ?? [] : null,
                RemoteBranches = data.BranchesProbed ? data.RemoteBranches ?? [] : null,
                Tags = data.BranchesProbed ? data.Tags ?? [] : null,
                BranchesProbed = data.BranchesProbed,
            }, new RepositoryStateWriteOptions { OperationStartedAt = startedAt }, cancellationToken);

            // A Worker answer without the branch lists (its ref listing failed) still refreshes the tag rows and
            // which tag is checked out, and leaves the branch rows as they are.
            if (!data.BranchesProbed && (data.Tags != null || onTag))
            {
                await PersistBranchesAsync(contextId, wrId, localBranches: null, remoteBranches: null, defaultBranchName: null,
                    tags: data.Tags, currentTag: data.CurrentTag, cancellationToken);
            }
        }

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

        _logger.LogDebug("Quick Fetch completed for workspace {WorkspaceName}", workspace.Name);

        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.error))
            .ToDictionary(r => r.RepositoryId, r => r.error!);
    }
}
