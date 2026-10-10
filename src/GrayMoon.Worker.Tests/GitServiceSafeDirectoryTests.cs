using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The global <c>safe.directory</c> write happens only when the probe proves dubious ownership. The probe and the write
/// stay on native git; a scripted command line stands in for git so no real ownership change is needed.
/// </summary>
public sealed class GitServiceSafeDirectoryTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("graymoon-safedir-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch { /* best-effort */ }
    }

    private (GitService Git, ScriptedCommandLine Script) Create(CommandLineResult probe)
    {
        var script = new ScriptedCommandLine(probe);
        var runner = new GitProcessRunner(script, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        return (new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService()), script);
    }

    [Fact]
    public async Task A_safe_repository_is_not_written_to_and_is_probed_once()
    {
        var (git, script) = Create(new CommandLineResult(0, "true", ""));

        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);
        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);

        Assert.Empty(script.SafeDirectoryWrites);
        Assert.Equal(1, script.Probes);
    }

    [Fact]
    public async Task Dubious_ownership_adds_the_directory_once_even_when_called_repeatedly()
    {
        var (git, script) = Create(new CommandLineResult(128, "", "fatal: detected dubious ownership in repository at 'x'\nTo add an exception for this directory, call:\n\tgit config --global --add safe.directory x"));

        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);
        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);
        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);

        Assert.Single(script.SafeDirectoryWrites);
        Assert.Contains("--global --add safe.directory", script.SafeDirectoryWrites[0], StringComparison.Ordinal);
        Assert.Equal(1, script.Probes);
    }

    [Theory]
    [InlineData(128, "fatal: not a git repository (or any of the parent directories): .git")]
    [InlineData(128, "fatal: bad config line 1 in file .git/config")]
    [InlineData(1, "something unrelated failed")]
    [InlineData(-1, "")]
    public async Task An_unrelated_failure_never_extends_trust_and_is_probed_again_next_time(int exitCode, string stderr)
    {
        var (git, script) = Create(new CommandLineResult(exitCode, "", stderr));

        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);
        await git.AddSafeDirectoryAsync(_repo, CancellationToken.None);

        Assert.Empty(script.SafeDirectoryWrites);
        Assert.Equal(2, script.Probes);
    }

    private sealed class ScriptedCommandLine(CommandLineResult probe) : ICommandLineService
    {
        public int Probes { get; private set; }

        public List<string> SafeDirectoryWrites { get; } = [];

        public Task<CommandLineResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? stdin = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
            => Task.FromResult(Handle(arguments));

        public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
            => Task.FromResult(Handle(string.Join(' ', arguments)));

        private CommandLineResult Handle(string arguments)
        {
            if (arguments.Contains("safe.directory", StringComparison.Ordinal))
            {
                SafeDirectoryWrites.Add(arguments);
                return new CommandLineResult(0, "", "");
            }

            if (arguments.Contains("rev-parse --is-inside-work-tree", StringComparison.Ordinal))
            {
                Probes++;
                return probe;
            }

            return new CommandLineResult(0, "", "");
        }
    }
}
