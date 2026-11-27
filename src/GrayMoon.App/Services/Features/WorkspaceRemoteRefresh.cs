using System.Collections.Concurrent;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// The remote-only observation of the special Workspace that the Feature removal queue runs to its end before it
/// removes a Feature: every repository is fetched, and what the fetch changes is stored (incoming and outgoing
/// commit counts, default-branch behind and ahead counts, remote branch rows with pruning, newer tags). The user
/// has just left the Feature and may create the next one right away, so the Workspace must show that it is behind
/// first.
/// <para>
/// It is not a Sync: no GitVersion, no project discovery, no file-version check and no dependency recompute run,
/// and no checkout is read again. A checkout GrayMoon did not see move is repaired where it matters, by Create
/// Feature, which compares its HEAD snapshot with the stored state and synchronizes the repositories that differ.
/// It takes no Workspace lock, because it writes only what a fetch observes and never what is checked out.
/// </para>
/// </summary>
public interface IWorkspaceRemoteRefresh
{
    /// <summary>
    /// Fetches every repository of the Workspace and stores the remote observations. A call that arrives while a
    /// refresh of the same Workspace is running joins that one. Throws when the refresh as a whole cannot run (no
    /// Worker, unknown Workspace); a repository whose fetch failed is logged and does not fail the others.
    /// </summary>
    Task RunAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceRemoteRefresh(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceRemoteRefresh> logger) : IWorkspaceRemoteRefresh
{
    // Workspace id -> the refresh that is running for it.
    private readonly ConcurrentDictionary<int, Lazy<Task>> _running = new();

    public Task RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var run = _running.GetOrAdd(
            workspaceId,
            _ => new Lazy<Task>(() => RunSharedAsync(workspaceId, contextId, cancellationToken)));
        return run.Value.WaitAsync(cancellationToken);
    }

    private async Task RunSharedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
    {
        // Leave the caller before the first step, so the entry is in the dictionary before it can be removed.
        await Task.Yield();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var errors = await git.QuickFetchAsync(workspaceId, contextId, repositoryIds: null, cancellationToken: cancellationToken);
            if (errors.Count > 0)
            {
                logger.LogWarning(
                    "Workspace remote refresh: the fetch failed for {FailedCount} repositories of workspace {WorkspaceId}; their stored counts are unchanged. First error: {Error}",
                    errors.Count, workspaceId, errors.Values.First());
            }
        }
        finally
        {
            _running.TryRemove(workspaceId, out _);
        }
    }
}
