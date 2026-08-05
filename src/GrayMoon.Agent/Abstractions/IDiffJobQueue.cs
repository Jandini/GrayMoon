namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Marker interface for the dedicated diff command queue (GetGitFileDiff). Kept separate from both
/// <see cref="IJobQueue"/> and <see cref="IReadJobQueue"/> so it resolves as its own DI service and gets
/// its own small worker pool, independent of the main command queue and the status-scan read pool.
/// </summary>
public interface IDiffJobQueue : IJobQueue
{
}
