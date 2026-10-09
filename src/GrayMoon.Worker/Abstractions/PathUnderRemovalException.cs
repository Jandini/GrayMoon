namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Thrown when work on a repository folder is refused or evicted because that folder is being removed. It is an
/// <see cref="OperationCanceledException"/> on purpose: every existing "this was cancelled" path (watcher scans, job
/// cancellation, Polly pipelines) already treats it as a quiet stop rather than a git failure.
/// </summary>
public sealed class PathUnderRemovalException(string path)
    : OperationCanceledException($"'{path}' is being removed.")
{
    public string Path { get; } = path;
}
