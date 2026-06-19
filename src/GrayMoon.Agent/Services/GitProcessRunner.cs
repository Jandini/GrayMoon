using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Common;
using Microsoft.Extensions.Logging;
using Polly;

namespace GrayMoon.Agent.Services;

public sealed class GitProcessRunner(ICommandLineService commandLine, ILogger<GitProcessRunner> logger)
{
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> SafeDirectoryPipeline =
        GitResiliencePipelines.CreateSafeDirectoryPipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> ClonePipeline =
        GitResiliencePipelines.CreateClonePipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> FetchPipeline =
        GitResiliencePipelines.CreateFetchPipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PullPipeline =
        GitResiliencePipelines.CreatePullPipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PushPipeline =
        GitResiliencePipelines.CreatePushPipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> LsRemotePipeline =
        GitResiliencePipelines.CreateLsRemotePipeline(logger);

    internal readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> MinimalFetchPipeline =
        GitResiliencePipelines.CreateMinimalFetchPipeline(logger);

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
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, null, ct, stderrAsOut, mirror);
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
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, stdinContent, ct, gitLike, gitLike);
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

    private static bool IsGitLikeProgressStreaming(string fileName, string arguments)
    {
        if (string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(fileName, "dotnet-gitversion", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
               && arguments.Contains("gitversion", StringComparison.OrdinalIgnoreCase);
    }
}
