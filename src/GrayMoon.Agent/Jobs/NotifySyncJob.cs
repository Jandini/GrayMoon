using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Jobs;

public sealed class NotifySyncJob : INotifyJob
{
    public required int RepositoryId { get; init; }
    public required int WorkspaceId { get; init; }
    public required string RepositoryPath { get; init; }
}
