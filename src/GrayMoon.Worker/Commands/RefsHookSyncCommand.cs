using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Handles the reference-transaction hook: a branch or remote-branch ref was created or deleted by something other than
/// GrayMoon (a terminal, an IDE, <c>git fetch --prune</c>). Re-lists the local and remote branches and reports them as a
/// branch-only snapshot, so the app replaces its persisted branch rows without learning anything else about the repository.
/// </summary>
/// <remarks>
/// The snapshot deliberately claims nothing but the branch lists: no identity, version, counts or tags. Refs are shared by
/// every worktree of a repository, so a branch created inside a Feature worktree says nothing about what the Workspace
/// checkout has open, and a tag list would make the app re-derive the pinned tag from the wrong checkout.
/// <para>
/// A burst of ref updates (a fetch that creates or prunes many branches runs the hook once per update) collapses into one
/// pass per repository: a short debounce lets the burst finish so the pass reads the final refs.
/// </para>
/// </remarks>
public sealed class RefsHookSyncCommand(
    IGitRepositoryReader reader,
    IHubConnectionProvider hubProvider,
    ILogger<RefsHookSyncCommand> logger,
    TimeSpan? debounce = null)
{
    /// <summary>Quiet time waited before reading the refs, so a burst of updates is read once.</summary>
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly RepositoryPassCoalescer _coalescer = new(debounce ?? DefaultDebounce);

    public Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("RefsHookSync job missing repositoryPath");
            return Task.CompletedTask;
        }

        return _coalescer.RunAsync(payload.RepositoryPath, ct => ReportBranchesAsync(payload, ct), cancellationToken);
    }

    private async Task ReportBranchesAsync(INotifyJob payload, CancellationToken cancellationToken)
    {
        var connection = hubProvider.Connection;
        if (connection?.State != HubConnectionState.Connected)
        {
            logger.LogWarning("Hub not connected, cannot send RefsHookSync SyncCommand");
            return;
        }

        // One listing, and null when git failed or the folder is gone. A failed read is "not probed", never an empty
        // list: the app replaces its branch rows with what is sent, so an empty list would delete all of them.
        var refs = await reader.GetRefSnapshotAsync(payload.RepositoryPath, cancellationToken);
        if (refs is null)
        {
            logger.LogWarning("RefsHookSync could not read the refs of {RepoPath}; branches left unprobed", payload.RepositoryPath);
            return;
        }
        var local = refs.LocalBranches;
        var remote = refs.RemoteBranches;

        var notification = new RepositorySyncNotification
        {
            WorkspaceId = payload.WorkspaceId,
            RepositoryId = payload.RepositoryId,
            RepositoryPath = payload.RepositoryPath,
            State = new RepositoryStateSnapshot
            {
                LocalBranches = [.. local],
                RemoteBranches = [.. remote],
                BranchesProbed = true
            }
        };
        await connection.InvokeAsync(WorkerHubMethods.SyncCommand, notification, cancellationToken);
        logger.LogInformation("RefsHookSync sent: workspace={WorkspaceId}, repo={RepoId}, local={Local}, remote={Remote}",
            payload.WorkspaceId, payload.RepositoryId, local.Count, remote.Count);
    }
}
