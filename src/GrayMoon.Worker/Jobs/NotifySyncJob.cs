using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Jobs;

public sealed class NotifySyncJob : INotifyJob
{
    public required int RepositoryId { get; init; }
    public required int WorkspaceId { get; init; }
    public required string RepositoryPath { get; init; }
    public NotifyHookKind HookKind { get; init; } = NotifyHookKind.Commit;

    /// <summary>
    /// A Checkout sync for a worktree Create Feature has just made at its base commit. Its refs are those of the
    /// repository it was created from and the Feature seed already holds projects and counts, so the sync skips the
    /// network fetch and the project scan and only computes what the seed cannot: the Feature branch version.
    /// </summary>
    public bool FreshWorktree { get; init; }
}
