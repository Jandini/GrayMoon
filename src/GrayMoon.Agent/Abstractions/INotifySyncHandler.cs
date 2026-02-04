namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Handler for notify jobs (e.g. NotifySync): runs GitVersion and invokes SyncCommand on the hub.
/// </summary>
public interface INotifySyncHandler
{
    Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default);
}
