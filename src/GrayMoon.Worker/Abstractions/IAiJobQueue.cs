namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Marker interface for the dedicated AI command queue (CollectGitEvidence, GenerateAiText, GetAiProviders). Kept
/// separate from <see cref="IJobQueue"/>, <see cref="IReadJobQueue"/> and <see cref="IDiffJobQueue"/> so it resolves as
/// its own DI service and gets its own small worker pool.
/// </summary>
public interface IAiJobQueue : IJobQueue
{
}
