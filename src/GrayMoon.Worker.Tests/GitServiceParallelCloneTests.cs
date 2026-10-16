using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitServiceParallelCloneTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("graymoon-clone-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Clones_into_the_same_workspace_folder_run_concurrently()
    {
        const int cloneCount = 3;
        var commandLine = new BarrierCommandLineService(cloneCount);
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());

        var clones = Enumerable.Range(0, cloneCount)
            .Select(i => git.CloneAsync(_workspace, $"https://example.invalid/repo{i}.git", null, CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(clones).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(cloneCount, commandLine.MaxConcurrent);
    }

    /// <summary>Completes each clone only once every clone is running, so a serialized clone never finishes.</summary>
    private sealed class BarrierCommandLineService(int expected) : ICommandLineService
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public int MaxConcurrent { get; private set; }

        public async Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            if (arguments.Contains("clone ", StringComparison.Ordinal))
            {
                var now = Interlocked.Increment(ref _running);
                lock (_allStarted)
                    MaxConcurrent = Math.Max(MaxConcurrent, now);
                if (now == expected)
                    _allStarted.TrySetResult();
                await _allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return new CommandLineResult(0, string.Empty, string.Empty);
        }

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
            => Task.FromResult(new CommandLineResult(0, string.Empty, string.Empty));
    }
}
