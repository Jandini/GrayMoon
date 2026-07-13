namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Marker interface for the dedicated read-only command queue (GetGitFileDiff, GetGitChangeStatus).
/// Kept separate from <see cref="IJobQueue"/> so it resolves as its own DI service and gets its own
/// small worker pool, independent of the main command queue's 16 workers.
/// </summary>
public interface IReadJobQueue : IJobQueue
{
}
