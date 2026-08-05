using System.Collections.Concurrent;
using System.Threading;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace GrayMoon.Agent.Services;

/// <summary>
/// Whether a <see cref="GitProcessRunner"/> invocation needs the per-repo write lock. <see cref="Read"/>
/// is for git commands that never write the index or working tree (diff/status/show/cat-file/rev-parse) -
/// these bypass <see cref="GitProcessRunner.RepoLocks"/> entirely so they are never blocked by a concurrent
/// checkout/merge/commit/push/fetch on the same repository. Callers passing <see cref="Read"/> must combine
/// it with the git <c>--no-optional-locks</c> flag so the command never attempts an opportunistic index
/// refresh that could collide with a concurrent writer's <c>index.lock</c>.
/// </summary>
public enum GitLockIntent
{
    Write,
    Read,
}

public sealed class GitProcessRunner(ICommandLineService commandLine, IOptions<GitProcessOptions> gitProcessOptions, ILogger<GitProcessRunner> logger)
{
    private static readonly string[] NetworkSubcommands = ["fetch", "pull", "push", "ls-remote"];

    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.DefaultTimeoutSeconds));
    private readonly TimeSpan _networkTimeout = TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds));
    private readonly TimeSpan _gitVersionTimeout = TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.GitVersionTimeoutSeconds));

    /// <summary>
    /// <see cref="Timeout.InfiniteTimeSpan"/> when <see cref="GitProcessOptions.CloneTimeoutSeconds"/> is
    /// 0 (the default) - both <see cref="CommandLineService"/>'s <c>CancellationTokenSource(TimeSpan)</c>
    /// constructor and Polly's <c>AddTimeout</c> understand that sentinel as "never time out" (the former
    /// natively; the latter is special-cased in <see cref="GitResiliencePipelines.CreateClonePipeline"/>
    /// since Polly rejects a non-positive duration), so a long-running clone is never killed or retried
    /// away just for taking a while.
    /// </summary>
    private readonly TimeSpan _cloneTimeout = ResolveCloneTimeout(gitProcessOptions.Value);

    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> SafeDirectoryPipeline =
        GitResiliencePipelines.CreateSafeDirectoryPipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> ClonePipeline =
        GitResiliencePipelines.CreateClonePipeline(logger, ResolveCloneTimeout(gitProcessOptions.Value));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> FetchPipeline =
        GitResiliencePipelines.CreateFetchPipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PullPipeline =
        GitResiliencePipelines.CreatePullPipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PushPipeline =
        GitResiliencePipelines.CreatePushPipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> LsRemotePipeline =
        GitResiliencePipelines.CreateLsRemotePipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> MinimalFetchPipeline =
        GitResiliencePipelines.CreateMinimalFetchPipeline(logger, TimeSpan.FromSeconds(Math.Max(1, gitProcessOptions.Value.NetworkTimeoutSeconds)));

    internal async Task<(int ExitCode, string? Stdout, string? Stderr)> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct,
        bool? streamStderrAsStdout = null,
        bool? mirrorFailureOutputAsStderr = null)
    {
        if (RequiresRepoLock(fileName, arguments) && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var repoLock = GetRepoLock(workingDirectory);
            await repoLock.WaitAsync(ct);
            try
            {
                return await RunCoreAsync(fileName, arguments, workingDirectory, ct, streamStderrAsStdout, mirrorFailureOutputAsStderr);
            }
            finally
            {
                repoLock.Release();
            }
        }

        return await RunCoreAsync(fileName, arguments, workingDirectory, ct, streamStderrAsStdout, mirrorFailureOutputAsStderr);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunCoreAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct,
        bool? streamStderrAsStdout = null,
        bool? mirrorFailureOutputAsStderr = null)
    {
        var gitLike = IsGitLikeProgressStreaming(fileName, arguments);
        var stderrAsOut = streamStderrAsStdout ?? gitLike;
        var mirror = mirrorFailureOutputAsStderr ?? gitLike;
        var timeout = ResolveTimeout(fileName, arguments);
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, null, ct, stderrAsOut, mirror, timeout);
        return (r.ExitCode, r.Stdout, r.Stderr);
    }

    internal async Task<(int ExitCode, string? Stdout, string? Stderr)> RunWithStdinAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        string stdinContent,
        CancellationToken ct)
    {
        if (RequiresRepoLock(fileName, arguments) && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var repoLock = GetRepoLock(workingDirectory);
            await repoLock.WaitAsync(ct);
            try
            {
                return await RunWithStdinCoreAsync(fileName, arguments, workingDirectory, stdinContent, ct);
            }
            finally
            {
                repoLock.Release();
            }
        }

        return await RunWithStdinCoreAsync(fileName, arguments, workingDirectory, stdinContent, ct);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunWithStdinCoreAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        string stdinContent,
        CancellationToken ct)
    {
        var gitLike = IsGitLikeProgressStreaming(fileName, arguments);
        var timeout = ResolveTimeout(fileName, arguments);
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, stdinContent, ct, gitLike, gitLike, timeout);
        return (r.ExitCode, r.Stdout, r.Stderr);
    }

    /// <summary>
    /// Argument-list based invocation for the Git Changes feature - never uses interpolated argument
    /// strings, so paths and commit messages are passed to the process verbatim with no shell/quoting
    /// risk. Mutating invocations (<paramref name="intent"/> = <see cref="GitLockIntent.Write"/>, the
    /// default) are serialized per-repository via <see cref="RepoLocks"/> like every other git
    /// invocation. Read-only invocations (<see cref="GitLockIntent.Read"/>) skip that lock entirely so
    /// they are never blocked by a concurrent write on the same repository - see <see cref="GitLockIntent"/>.
    /// </summary>
    internal async Task<(int ExitCode, string? Stdout, string? Stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        byte[]? stdinBytes,
        CancellationToken ct,
        GitLockIntent intent = GitLockIntent.Write)
    {
        if (intent == GitLockIntent.Write && RequiresRepoLock(fileName) && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var repoLock = GetRepoLock(workingDirectory);
            await repoLock.WaitAsync(ct);
            try
            {
                return await RunCoreAsync(fileName, arguments, workingDirectory, stdinBytes, ct);
            }
            finally
            {
                repoLock.Release();
            }
        }

        return await RunCoreAsync(fileName, arguments, workingDirectory, stdinBytes, ct);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        byte[]? stdinBytes,
        CancellationToken ct)
    {
        var gitLike = string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase);
        var timeout = ResolveTimeout(fileName, arguments);
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, stdinBytes, ct, gitLike, gitLike, timeout);
        return (r.ExitCode, r.Stdout, r.Stderr);
    }

    internal void ReportOverlayStderr(string message)
    {
        var sink = CommandLineStreamAmbient.Current.Value;
        if (sink == null || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            sink(new CommandLineStreamEvent(AgentCommandStreamKind.Stderr, message.Trim()));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to report overlay stderr: {Message}", message);
        }
    }

    private static SemaphoreSlim GetRepoLock(string repoPath)
    {
        var key = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return RepoLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    // dotnet-gitversion reads git HEAD/index state, so it must be serialized per-repo
    // the same way raw git commands are.
    private static bool RequiresRepoLock(string fileName, string arguments)
        => string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "dotnet-gitversion", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("gitversion", StringComparison.OrdinalIgnoreCase));

    private static bool RequiresRepoLock(string fileName)
        => string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitLikeProgressStreaming(string fileName, string arguments)
    {
        if (string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(fileName, "dotnet-gitversion", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
               && arguments.Contains("gitversion", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the timeout tier for one invocation: GitVersion (scans full history, can be slower than a
    /// plain git call), clone (its own unbounded-by-default tier - see <see cref="_cloneTimeout"/>),
    /// network (fetch/pull/push/ls-remote - can legitimately take longer on large repos/slow networks),
    /// or the default (everything else - status, diff/show, rev-parse, cat-file, add, restore, reset,
    /// commit, branch/tag queries, config, etc.).
    /// </summary>
    internal TimeSpan ResolveTimeout(string fileName, string arguments)
    {
        if (IsGitVersionInvocation(fileName, arguments))
            return _gitVersionTimeout;

        if (string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
        {
            if (StartsWithSubcommand(arguments, "clone"))
                return _cloneTimeout;

            if (StartsWithNetworkSubcommand(arguments))
                return _networkTimeout;
        }

        return _defaultTimeout;
    }

    internal TimeSpan ResolveTimeout(string fileName, IReadOnlyList<string> arguments)
    {
        if (string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
        {
            if (StartsWithSubcommand(arguments, "clone"))
                return _cloneTimeout;

            if (StartsWithNetworkSubcommand(arguments))
                return _networkTimeout;
        }

        return _defaultTimeout;
    }

    private static bool IsGitVersionInvocation(string fileName, string arguments)
        => string.Equals(fileName, "dotnet-gitversion", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("gitversion", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 0 (or negative) means "no timeout" per <see cref="GitProcessOptions.CloneTimeoutSeconds"/>'s doc -
    /// <see cref="Timeout.InfiniteTimeSpan"/> is the sentinel both <c>CancellationTokenSource(TimeSpan)</c>
    /// and (with explicit handling) Polly's <c>AddTimeout</c> treat as "never fires".
    /// </summary>
    private static TimeSpan ResolveCloneTimeout(GitProcessOptions options)
        => options.CloneTimeoutSeconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(options.CloneTimeoutSeconds);

    /// <summary>
    /// Every fetch/pull/push/ls-remote invocation in this codebase passes the subcommand as the first
    /// token with no leading global options (see <c>GitService.cs</c>), so a simple leading-token check
    /// is sufficient here - this is not a general-purpose git argument parser.
    /// </summary>
    private static bool StartsWithNetworkSubcommand(string arguments)
    {
        foreach (var sub in NetworkSubcommands)
        {
            if (StartsWithSubcommand(arguments, sub))
                return true;
        }

        return false;
    }

    private static bool StartsWithNetworkSubcommand(IReadOnlyList<string> arguments)
    {
        foreach (var arg in arguments)
        {
            if (arg.Length == 0)
                continue;

            return Array.IndexOf(NetworkSubcommands, arg) >= 0;
        }

        return false;
    }

    private static bool StartsWithSubcommand(string arguments, string subcommand)
    {
        var trimmed = arguments.TrimStart();
        return trimmed.StartsWith(subcommand, StringComparison.Ordinal)
            && (trimmed.Length == subcommand.Length || trimmed[subcommand.Length] == ' ');
    }

    private static bool StartsWithSubcommand(IReadOnlyList<string> arguments, string subcommand)
    {
        foreach (var arg in arguments)
        {
            if (arg.Length == 0)
                continue;

            return string.Equals(arg, subcommand, StringComparison.Ordinal);
        }

        return false;
    }
}
