using GrayMoon.Abstractions.Notifications;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// The two steps of a hook sync notification, as <see cref="WorkerSyncNotificationQueue"/> runs them: the state of
/// each repository as it arrives, then one recompute and one set of broadcasts for the context.
/// </summary>
public interface ISyncNotificationHandler
{
    /// <summary>
    /// The per-repository part: attributes the notification to a context and stores that repository's state.
    /// Reports to <paramref name="observer"/> which context it belongs to and that the state was stored. A
    /// notification that is skipped, superseded or taken back by the observer never reports a state write.
    /// </summary>
    /// <param name="n">The notification the Worker sent.</param>
    /// <param name="receivedAt">
    /// <see cref="GrayMoon.App.Services.Workspaces.RepositoryStateFreshness.Now"/> read when the notification reached
    /// the App, before it waited in the sync queue. Null means it is applied as it stands.
    /// </param>
    /// <param name="observer">Receives the context and the state write.</param>
    Task ApplyAsync(RepositorySyncNotification n, long? receivedAt, ISyncApplyObserver observer);

    /// <summary>
    /// The per-context part for notifications of one context whose state is stored: the recompute once (when any of
    /// them needs it), then <c>ContextRepositorySynced</c> per repository and <c>ContextSynced</c> once. The
    /// broadcasts are sent also when the recompute throws.
    /// </summary>
    Task CompleteAsync(IReadOnlyList<AppliedSync> items);
}

/// <summary>What <see cref="ISyncNotificationHandler.ApplyAsync"/> reports while it applies one notification.</summary>
public interface ISyncApplyObserver
{
    /// <summary>
    /// The notification belongs to this context. Called before the state write. <paramref name="finalization"/> is set
    /// while the Feature of that context is still being created or repaired: it completes with true when that has
    /// ended, and with false when the wait for it ran out. Returns true to store the notification's state now, false
    /// to take the notification back unapplied; the caller then applies it again later. The handler never waits for
    /// the finalization itself, so the observer decides who waits: a single apply waits here, the sync queue sets the
    /// notification aside and goes on with the other contexts.
    /// </summary>
    Task<bool> ContextResolvedAsync(int workspaceId, WorkspaceFeatureContextId contextId, Task<bool>? finalization);

    /// <summary>The repository's state is stored. Called once, straight after the state write.</summary>
    void StateWritten(AppliedSync applied);
}

/// <summary>One notification whose repository state is stored and still needs the completion of its context.</summary>
/// <param name="NeedsRecompute">False for a refs-only notification (branch lists and nothing else).</param>
/// <param name="Changed">
/// The groups of the repository's state that the write changed; none for a notification that only confirmed what
/// was stored. Null when the writer did not say (the completion then treats everything probed as changed).
/// </param>
public sealed record AppliedSync(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    bool IsSpecialWorkspace,
    int RepositoryId,
    bool NeedsRecompute,
    GrayMoon.App.Services.Workspaces.RepositoryFactGroups? Changed = null);
