namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Job from HTTP /hook/* (e.g. NotifySync): no request ID; worker pushes SyncCommand to the app.
/// </summary>
public interface INotifyJob : IJob
{
    int RepositoryId { get; }
    int WorkspaceId { get; }
    string RepositoryPath { get; }
    NotifyHookKind HookKind { get; }
}
