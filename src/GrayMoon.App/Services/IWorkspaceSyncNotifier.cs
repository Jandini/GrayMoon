namespace GrayMoon.App.Services;

public interface IWorkspaceSyncNotifier
{
    void NotifySyncCompleted(int workspaceId);
    Task WaitForSyncAsync(int workspaceId, CancellationToken cancellationToken = default);
}
