using System.Text;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GitServiceDeleteBranchTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly RecordingCommandLineService _recorder;
    private readonly GitService _git;

    public GitServiceDeleteBranchTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLineService(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task RemoteDelete_WithBearerToken_UsesAuthHeaderArgs_AndPreservesPushDeleteAndHooks()
    {
        _repo.CommitInitial();
        const string token = "test-bearer-token-value";

        var (success, error) = await _git.DeleteBranchAsync(
            _repo.RepositoryPath,
            "feature/matt",
            isRemote: true,
            force: false,
            CancellationToken.None,
            skipHooks: true,
            bearerToken: token);

        Assert.True(success);
        Assert.Null(error);

        var push = Assert.Single(_recorder.StringArgumentCalls, c => c.Contains("push origin --delete", StringComparison.Ordinal));
        Assert.Contains("push origin --delete feature/matt", push, StringComparison.Ordinal);
        Assert.Contains("http.extraHeader=", push, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token)), push, StringComparison.Ordinal);
        Assert.Contains("core.askpass=true", push, StringComparison.Ordinal);
        Assert.Contains("credential.helper=", push, StringComparison.Ordinal);
        Assert.Contains("core.hooksPath=", push, StringComparison.Ordinal);
        Assert.Contains("GrayMoon-empty-hooks", push, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalDelete_DoesNotReceiveAuthArgs()
    {
        _repo.CommitInitial();
        _repo.RunGit("branch", "feature/local-only");

        var (success, error) = await _git.DeleteBranchAsync(
            _repo.RepositoryPath,
            "feature/local-only",
            isRemote: false,
            force: false,
            CancellationToken.None,
            bearerToken: "should-not-appear");

        Assert.True(success);
        Assert.Null(error);

        var delete = Assert.Single(_recorder.StringArgumentCalls, c => c.Contains("branch -d", StringComparison.Ordinal));
        Assert.Equal("branch -d feature/local-only", delete);
        Assert.DoesNotContain("http.extraHeader=", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-appear", delete, StringComparison.Ordinal);
    }

    private sealed class RecordingCommandLineService(ICommandLineService inner) : ICommandLineService
    {
        public List<string> StringArgumentCalls { get; } = [];

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            StringArgumentCalls.Add(arguments);
            // Remote delete needs a network push; short-circuit so the test stays offline.
            if (arguments.Contains("push origin --delete", StringComparison.Ordinal))
                return Task.FromResult(new CommandLineResult(0, "", ""));

            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
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
            => inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
    }
}
