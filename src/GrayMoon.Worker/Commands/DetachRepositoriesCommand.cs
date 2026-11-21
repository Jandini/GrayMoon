using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// The Worker's side of "these repositories leave this Workspace". In one command, so nothing can start watching a
/// folder again in between: an exclusive claim on the folders releases the Worker's file watchers and stops its own
/// processes there, the Git hooks GrayMoon installed are removed inside that claim, and the claim is lifted. Nothing
/// else on disk is touched and no folder is deleted. A repository that fails does not stop the others, so a retry
/// after fixing the cause finishes the job.
/// </summary>
public sealed class DetachRepositoriesCommand(
    IGitService git,
    IRepositoryAccess access,
    IGitChangesMonitoring monitoring,
    IWorkspaceCapabilityProvider capabilityProvider,
    ILogger<DetachRepositoriesCommand> logger) : ICommandHandler<DetachRepositoriesRequest, DetachRepositoriesResponse>
{
    private const int DefaultMaxConcurrentRepos = 8;

    public async Task<DetachRepositoriesResponse> ExecuteAsync(DetachRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);

        // Git is asked where each repository keeps its hooks before the claim: no process may start inside it.
        var maxConcurrent = request.MaxParallelOperations is > 0 ? request.MaxParallelOperations.Value : DefaultMaxConcurrentRepos;
        using var semaphore = new SemaphoreSlim(maxConcurrent);
        var items = await Task.WhenAll(request.Repositories.Select(repository => ResolveAsync(request, workspacePath, repository, semaphore, cancellationToken)));

        var claimPaths = items
            .Where(item => item.Target is not null)
            .Select(item => RepositoryPathKey.Normalize(item.Target!.RepositoryPath))
            .ToList();
        if (request.WorkspaceRemoved)
            claimPaths.AddRange(monitoring.GetMonitored(request.WorkspaceId).Select(m => RepositoryPathKey.Normalize(m.Path)));
        claimPaths = claimPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var response = new DetachRepositoriesResponse();
        if (claimPaths.Count == 0)
        {
            response.Results.AddRange(items.Select(item => ToResult(item, item.Failure!)));
            return Finish(request, response);
        }

        var watchedBefore = WatchedUnder(claimPaths);

        IRepositoryExclusiveScope claim;
        try
        {
            claim = await access.AcquireExclusiveAsync(claimPaths, cancellationToken);
        }
        catch (RepositoryAccessException ex)
        {
            logger.LogError(ex, "Could not release the Worker's own handles for workspace {WorkspaceId}", request.WorkspaceId);
            response.Results.AddRange(items.Select(item => ToResult(item, item.Failure ?? Busy(item, ex.Message))));
            return Finish(request, response);
        }

        using (claim)
        {
            // Still watched after the claim means a watcher did not let go: the folder would stay locked.
            var stillWatched = WatchedUnder(claimPaths);
            foreach (var item in items)
            {
                if (item.Failure is not null)
                {
                    response.Results.Add(ToResult(item, item.Failure));
                    continue;
                }

                var key = RepositoryPathKey.Normalize(item.Target!.RepositoryPath);
                var held = stillWatched.FirstOrDefault(path => RepositoryPathKey.IsUnderOrEqual(path, key));
                response.Results.Add(ToResult(item, held is not null
                    ? SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.WorkerBusy, held, "A file watcher on this folder was not released.")
                    : git.RemoveSyncHooks(item.Target)));
            }

            response.ReleasedWatchers.AddRange(watchedBefore.Except(stillWatched, StringComparer.OrdinalIgnoreCase));
        }

        return Finish(request, response);
    }

    private DetachRepositoriesResponse Finish(DetachRepositoriesRequest request, DetachRepositoriesResponse response)
    {
        response.Success = response.Results.All(result => result.Outcome != nameof(SyncHookRemovalOutcome.Failed));
        if (response.Success && request.WorkspaceRemoved)
            capabilityProvider.Invalidate(request.WorkspaceId);

        logger.LogInformation(
            "DetachRepositories done: workspace={WorkspaceId} workspaceRemoved={WorkspaceRemoved} repos={Count} {Outcomes} releasedWatchers={ReleasedWatchers}",
            request.WorkspaceId, request.WorkspaceRemoved, response.Results.Count,
            string.Join(' ', response.Results.GroupBy(r => r.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")),
            response.ReleasedWatchers.Count);
        return response;
    }

    private async Task<Item> ResolveAsync(
        DetachRepositoriesRequest request, string workspacePath, DetachRepositoriesRepository repository, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        string repoPath;
        try
        {
            repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repository.RepositoryName ?? "", request.WorkspaceRepositoryName);
        }
        catch (ArgumentException ex)
        {
            return new Item(repository, null, SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.InvalidName, null, ex.Message));
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var target = await git.ResolveSyncHookRemovalAsync(repoPath, cancellationToken);
            // A git failure is final for this repository; it still takes part in the claim so its watcher is released.
            return new Item(repository, target, target.Settled is { Succeeded: false } failed ? failed : null);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private List<string> WatchedUnder(IReadOnlyCollection<string> claimPaths) =>
        monitoring.GetMonitored()
            .Select(m => RepositoryPathKey.Normalize(m.Path))
            .Where(path => claimPaths.Any(claimed => RepositoryPathKey.IsUnderOrEqual(path, claimed)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static SyncHookRemovalResult Busy(Item item, string message) =>
        SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.WorkerBusy, item.Target?.RepositoryPath, message);

    private static DetachRepositoriesResult ToResult(Item item, SyncHookRemovalResult result) => new()
    {
        RepositoryId = item.Repository.RepositoryId,
        RepositoryName = item.Repository.RepositoryName,
        Path = item.Target?.RepositoryPath,
        Outcome = result.Outcome.ToString(),
        FailureKind = result.Outcome == SyncHookRemovalOutcome.Failed ? result.FailureKind.ToString() : null,
        FailedPath = result.FailedPath,
        ErrorMessage = result.ErrorMessage,
        RemovedHooks = [.. result.RemovedHooks],
        RestoredHooks = [.. result.RestoredHooks],
        ForeignHooks = [.. result.ForeignHooks],
        LeftoverReplacedHooks = [.. result.LeftoverReplacedHooks],
    };

    /// <summary>One requested repository: where its hooks are, or why it already failed.</summary>
    private sealed record Item(DetachRepositoriesRepository Repository, SyncHookRemovalTarget? Target, SyncHookRemovalResult? Failure);
}
