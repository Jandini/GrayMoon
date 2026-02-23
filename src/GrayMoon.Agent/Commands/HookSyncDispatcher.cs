using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Commands;

/// <summary>Routes incoming notify jobs to the appropriate hook handler based on <see cref="INotifyJob.HookKind"/>.</summary>
public sealed class HookSyncDispatcher(
    CheckoutHookSyncCommand checkoutHandler,
    CommitHookSyncCommand commitHandler,
    MergeHookSyncCommand mergeHandler) : INotifySyncHandler
{
    public Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
        => payload.HookKind switch
        {
            NotifyHookKind.Checkout => checkoutHandler.ExecuteAsync(payload, cancellationToken),
            NotifyHookKind.Merge    => mergeHandler.ExecuteAsync(payload, cancellationToken),
            _                       => commitHandler.ExecuteAsync(payload, cancellationToken),
        };
}
