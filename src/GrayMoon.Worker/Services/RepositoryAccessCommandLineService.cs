using GrayMoon.Common;
using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Services;

/// <summary>
/// The choke point for every process the Worker launches: a process with a working directory pins that folder for its
/// whole life, so it runs under a shared <see cref="IRepositoryAccess"/> lease on that directory. A folder being removed
/// refuses new processes (<see cref="PathUnderRemovalException"/>) and evicts running ones: the lease's yield token
/// cancels the call, and <see cref="CommandLineService"/> kills the process tree on cancellation. Only processes started
/// here are ever terminated.
/// </summary>
public sealed class RepositoryAccessCommandLineService(ICommandLineService inner, IRepositoryAccess access) : ICommandLineService
{
    public Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false,
        TimeSpan? timeout = null)
        => RunGuardedAsync(
            workingDirectory,
            GitAccessClassifier.Classify(fileName, arguments),
            cancellationToken,
            ct => inner.RunAsync(fileName, arguments, workingDirectory, stdin, ct, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout));

    public Task<CommandLineResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        byte[]? stdinBytes = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false,
        TimeSpan? timeout = null)
        => RunGuardedAsync(
            workingDirectory,
            GitAccessClassifier.Classify(fileName, arguments),
            cancellationToken,
            ct => inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, ct, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout));

    private async Task<CommandLineResult> RunGuardedAsync(
        string? workingDirectory,
        RepositoryAccessKind kind,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<CommandLineResult>> run)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return await run(cancellationToken);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lease = access.TryAcquireShared(workingDirectory, kind, forceTerminate: () => Cancel(linked))
                          ?? throw new PathUnderRemovalException(workingDirectory);
        using var evict = lease.Yield.Register(() => Cancel(linked));
        try
        {
            return await run(linked.Token);
        }
        catch (OperationCanceledException) when (lease.Yield.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new PathUnderRemovalException(workingDirectory);
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call already finished; nothing to cancel.
        }
    }
}

/// <summary>
/// Decides whether a process only reads its working directory (cancelled at once when the folder is removed) or may
/// change it (given a grace period first). Anything not known to be read-only counts as changing.
/// </summary>
public static class GitAccessClassifier
{
    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "diff-tree", "diff-index", "log", "show", "rev-parse", "rev-list", "cat-file", "for-each-ref",
        "ls-files", "ls-tree", "ls-remote", "merge-base", "describe", "name-rev", "blame", "shortlog", "grep",
        "check-ignore", "show-ref", "count-objects", "var", "version", "--version", "cherry", "range-diff", "whatchanged",
    };

    // Options that take their value as the next token and so must be skipped when looking for the subcommand.
    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.OrdinalIgnoreCase) { "-c", "-C", "--git-dir", "--work-tree", "--namespace" };

    public static RepositoryAccessKind Classify(string fileName, string? arguments) =>
        Classify(fileName, Tokenize(arguments));

    public static RepositoryAccessKind Classify(string fileName, IReadOnlyList<string>? arguments)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!string.Equals(name, "git", StringComparison.OrdinalIgnoreCase) || arguments is null)
        {
            return RepositoryAccessKind.Mutating;
        }

        var index = 0;
        while (index < arguments.Count && arguments[index].StartsWith('-'))
        {
            var option = arguments[index];
            index += OptionsWithValue.Contains(option) ? 2 : 1;
            if (option.Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryAccessKind.ReadOnly;
            }
        }

        if (index >= arguments.Count)
        {
            return RepositoryAccessKind.Mutating;
        }

        var sub = arguments[index];
        var rest = arguments.Skip(index + 1).ToList();
        if (ReadOnlyGit.Contains(sub))
        {
            return RepositoryAccessKind.ReadOnly;
        }

        return sub.ToLowerInvariant() switch
        {
            "branch" => rest.Any(a => a is "--show-current" or "--list" or "-l" or "--contains" or "--merged" or "--no-merged" or "-a" or "-r" or "-v" or "-vv")
                ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            "worktree" => rest.FirstOrDefault() == "list" ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            "config" => rest.Any(a => a is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
                ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            "stash" => rest.FirstOrDefault() == "list" ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            "remote" => rest.Count == 0 || rest.All(a => a is "-v" or "--verbose") || rest.FirstOrDefault() is "show" or "get-url"
                ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            "tag" => rest.Count == 0 || rest.Any(a => a is "-l" or "--list" or "--contains")
                ? RepositoryAccessKind.ReadOnly : RepositoryAccessKind.Mutating,
            _ => RepositoryAccessKind.Mutating,
        };
    }

    private static IReadOnlyList<string> Tokenize(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? []
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
