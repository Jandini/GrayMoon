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
    /// <summary>
    /// Runs git fetch + tag list + commit counts for every repo in the workspace (no GitVersion, no csproj scan,
    /// no branch listing). Updates only commit-count fields and RepositoryBranch tag rows in the DB.
    /// Significantly faster than a full Sync. Pass <paramref name="repositoryIds"/> to restrict the fetch to
    /// a specific subset (e.g. one dependency level); null fetches every repository in the workspace.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, string>> QuickFetchAsync(
        int workspaceId,
        IReadOnlyCollection<int>? repositoryIds = null,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, cancellationToken);

        var links = workspace.Repositories
            .Where(l => l.Repository != null && (repositoryIds == null || repositoryIds.Contains(l.RepositoryId)))
            .ToList();

        if (links.Count == 0)
            return new Dictionary<int, string>();

        _logger.LogInformation("Quick Fetch triggered. Workspace={WorkspaceName}, RepoCount={Count}", workspace.Name, links.Count);

        var completedCount = 0;
        var totalCount = links.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);

        var fetchTasks = links.Select(async link =>
        {
            var repo = link.Repository!;
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var args = new
                {
                    workspaceName = workspaceFolderName,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("FetchCommits", args, cancellationToken);
                var data = response.Data != null
                    ? AgentResponseJson.DeserializeAgentResponse<AgentFetchCommitsResponse>(response.Data)
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

        // Update commit-count fields on WorkspaceRepositoryLink rows.
        // EF Core not thread-safe; query sequentially after the parallel fetch.
        var repoIds = results.Select(r => r.RepositoryId).ToList();
        var wrLinks = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        foreach (var (_, repoId, data, error) in results)
        {
            if (data == null || !string.IsNullOrWhiteSpace(error)) continue;
            var wr = wrLinks.FirstOrDefault(w => w.RepositoryId == repoId);
            if (wr == null) continue;

            if (data.OutgoingCommits.HasValue) wr.OutgoingCommits = data.OutgoingCommits;
            if (data.IncomingCommits.HasValue) wr.IncomingCommits = data.IncomingCommits;
            if (data.HasUpstream.HasValue) wr.BranchHasUpstream = data.HasUpstream.Value;
            if (data.DefaultBranchBehind.HasValue) wr.DefaultBranchBehindCommits = data.DefaultBranchBehind;
            if (data.DefaultBranchAhead.HasValue) wr.DefaultBranchAheadCommits = data.DefaultBranchAhead;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Update RepositoryBranch tag rows and HasNewerTag. Pass localBranches/remoteBranches as null so
        // existing branch rows are not touched - only tag rows are refreshed.
        foreach (var (wrId, _, data, error) in results)
        {
            if (!string.IsNullOrWhiteSpace(error)) continue;
            if (data?.Tags == null && string.IsNullOrWhiteSpace(data?.CurrentTag)) continue;
            await PersistBranchesAsync(wrId, localBranches: null, remoteBranches: null, defaultBranchName: null,
                tags: data?.Tags, currentTag: data?.CurrentTag, cancellationToken);
        }

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

        _logger.LogDebug("Quick Fetch completed for workspace {WorkspaceName}", workspace.Name);

        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.error))
            .ToDictionary(r => r.RepositoryId, r => r.error!);
    }
}
