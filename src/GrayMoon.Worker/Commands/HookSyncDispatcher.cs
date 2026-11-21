using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Routes incoming notify jobs to the appropriate hook handler based on <see cref="INotifyJob.HookKind"/>. A job of a
/// workspace the App says does not exist is dropped here, before any git, GitVersion or project scan runs: its hook
/// was left behind by a workspace removed before hooks were removed with it. The hook file itself is left alone;
/// the Worker deletes a hook only as part of an operation the user started.
/// </summary>
public sealed class HookSyncDispatcher(
    CheckoutHookSyncCommand checkoutHandler,
    CommitHookSyncCommand commitHandler,
    MergeHookSyncCommand mergeHandler,
    PushHookSyncCommand pushHandler,
    RefsHookSyncCommand refsHandler,
    IWorkspaceCapabilityProvider? capabilityProvider = null,
    ILogger<HookSyncDispatcher>? logger = null) : INotifySyncHandler
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (capabilityProvider is not null && await capabilityProvider.IsWorkspaceGoneAsync(payload.WorkspaceId, cancellationToken))
        {
            logger?.LogInformation(
                "Dropped {HookKind} hook: the App has no workspace {WorkspaceId} (repository {RepositoryId}, {RepoPath}). The hook is left over from a removed workspace.",
                payload.HookKind, payload.WorkspaceId, payload.RepositoryId, payload.RepositoryPath);
            return;
        }

        await (payload.HookKind switch
        {
            NotifyHookKind.Checkout => checkoutHandler.ExecuteAsync(payload, cancellationToken),
            NotifyHookKind.Merge => mergeHandler.ExecuteAsync(payload, cancellationToken),
            NotifyHookKind.Push => pushHandler.ExecuteAsync(payload, cancellationToken),
            NotifyHookKind.Refs => refsHandler.ExecuteAsync(payload, cancellationToken),
            _ => commitHandler.ExecuteAsync(payload, cancellationToken),
        });
    }
}
