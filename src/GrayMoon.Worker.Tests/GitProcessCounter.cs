using System.Collections.Concurrent;
using GrayMoon.Common;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Records every process the Worker starts through <see cref="ICommandLineService"/>, so a test can assert how many
/// git processes an operation costs and which ones. It is the shared form of the recorder in
/// <c>FeatureWorktreeGitProcessCountTests</c>; the command-count gates of the reactive work use it.
/// </summary>
internal sealed class GitProcessCounter(ICommandLineService inner) : ICommandLineService
{
    private readonly ConcurrentQueue<GitProcessCall> _calls = new();

    /// <summary>Every recorded process, in start order.</summary>
    public IReadOnlyList<GitProcessCall> Calls => [.. _calls];

    public int Count => _calls.Count;

    public void Clear() => _calls.Clear();

    /// <summary>How many recorded processes carry <paramref name="marker"/> in their arguments (for example "worktree list").</summary>
    public int CountOf(string marker)
        => _calls.Count(c => c.Arguments.Contains(marker, StringComparison.Ordinal));

    /// <summary>The recorded processes whose working directory is <paramref name="workingDirectory"/>.</summary>
    public IReadOnlyList<GitProcessCall> In(string workingDirectory)
    {
        var expected = Normalize(workingDirectory);
        return [.. _calls.Where(c => c.WorkingDirectory is not null
            && string.Equals(Normalize(c.WorkingDirectory), expected, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>One line per process, for an assertion message.</summary>
    public string Describe() => string.Join(Environment.NewLine, _calls.Select(c => c.Arguments));

    public Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false,
        TimeSpan? timeout = null,
        TimeSpan? idleTimeout = null)
    {
        _calls.Enqueue(new GitProcessCall(fileName, arguments, workingDirectory));
        return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout, idleTimeout);
    }

    public Task<CommandLineResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        byte[]? stdin = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false,
        TimeSpan? timeout = null,
        TimeSpan? idleTimeout = null)
    {
        _calls.Enqueue(new GitProcessCall(fileName, string.Join(' ', arguments), workingDirectory));
        return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout, idleTimeout);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>One process start seen by <see cref="GitProcessCounter"/>.</summary>
internal sealed record GitProcessCall(string FileName, string Arguments, string? WorkingDirectory);
