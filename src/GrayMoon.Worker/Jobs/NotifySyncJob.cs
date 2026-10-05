using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Jobs;

public sealed class NotifySyncJob : INotifyJob
{
    public required int RepositoryId { get; init; }
    public required int WorkspaceId { get; init; }
    public required string RepositoryPath { get; init; }
    public NotifyHookKind HookKind { get; init; } = NotifyHookKind.Commit;
}
