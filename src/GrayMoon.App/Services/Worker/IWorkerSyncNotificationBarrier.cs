namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Lets an operation that decides from stored repository state (versions, commit counts, upstream) wait until every
/// worker sync notification the App has already received for that workspace has been applied. Without it, a hook sync
/// that is still queued is applied part-way through the operation and either hides work (the operation read the
/// state before it landed) or overwrites fresher state the operation itself just wrote.
/// </summary>
public interface IWorkerSyncNotificationBarrier
{
    /// <summary>Completes once no received sync notification for <paramref name="workspaceId"/> is queued or being applied.</summary>
    Task WaitForPendingAsync(int workspaceId, CancellationToken cancellationToken);
}
