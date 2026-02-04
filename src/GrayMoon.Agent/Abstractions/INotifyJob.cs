namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Job from HTTP /notify (e.g. NotifySync): no request ID; agent pushes SyncCommand to the app.
/// </summary>
public interface INotifyJob : IJob
{
    int RepositoryId { get; }
    int WorkspaceId { get; }
    string RepositoryPath { get; }
}
