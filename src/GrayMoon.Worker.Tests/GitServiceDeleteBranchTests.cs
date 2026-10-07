using System.Text;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitServiceDeleteBranchTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly RecordingCommandLineService _recorder;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;

    public GitServiceDeleteBranchTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLineService(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
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
        Assert.Contains("core.hooksPath=", push, StringComparison.Ordinal);
        Assert.Contains("GrayMoon-empty-hooks", push, StringComparison.Ordinal);

        // The token is delivered through GIT_CONFIG_* (git 2.31+) or -c arguments (older git), never both,
        // and never in plain text.
        Assert.DoesNotContain(token, push, StringComparison.Ordinal);
        var expectedHeader = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        var env = _recorder.AmbientEnvironmentFor(push);
        if (env != null)
        {
            Assert.DoesNotContain("http.extraHeader", push, StringComparison.Ordinal);
            Assert.Contains(expectedHeader, env.Values);
            Assert.Contains("http.extraHeader", env.Values);
            Assert.Contains("credential.helper", env.Values);
        }
        else
        {
            Assert.Contains("http.extraHeader=", push, StringComparison.Ordinal);
            Assert.Contains(expectedHeader.Substring("Authorization: Basic ".Length), push, StringComparison.Ordinal);
            Assert.Contains("core.askpass=true", push, StringComparison.Ordinal);
            Assert.Contains("credential.helper=", push, StringComparison.Ordinal);
        }
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

    [Fact]
    public async Task RemoteDelete_WithExpectedSha_UsesForceWithLease_AfterFetch()
    {
        _repo.CommitInitial();
        const string token = "lease-token";
        const string sha = "abc123def456";

        var (success, error) = await _git.DeleteBranchAsync(
            _repo.RepositoryPath,
            "feature/lease",
            isRemote: true,
            force: false,
            CancellationToken.None,
            skipHooks: true,
            bearerToken: token,
            expectedSha: sha);

        Assert.True(success);
        Assert.Null(error);

        Assert.Contains(_recorder.StringArgumentCalls, c => c.Contains("fetch", StringComparison.OrdinalIgnoreCase));
        var push = Assert.Single(_recorder.StringArgumentCalls, c => c.Contains("--force-with-lease=", StringComparison.Ordinal));
        Assert.Contains($"--force-with-lease=refs/heads/feature/lease:{sha}", push, StringComparison.Ordinal);
        Assert.Contains("origin :feature/lease", push, StringComparison.Ordinal);
        Assert.True(
            _recorder.AmbientEnvironmentFor(push) != null || push.Contains("http.extraHeader=", StringComparison.Ordinal),
            "The lease push must carry the connector token.");
    }

    [Fact]
    public async Task RemoteDelete_WithoutExpectedSha_StillUsesPushDelete()
    {
        _repo.CommitInitial();

        var (success, error) = await _git.DeleteBranchAsync(
            _repo.RepositoryPath,
            "feature/legacy",
            isRemote: true,
            force: false,
            CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        Assert.Contains(_recorder.StringArgumentCalls, c => c.Contains("push origin --delete feature/legacy", StringComparison.Ordinal));
    }

    private sealed class RecordingCommandLineService(ICommandLineService inner) : ICommandLineService
    {
        public List<string> StringArgumentCalls { get; } = [];

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>?> _ambientEnvironment = new();

        /// <summary>The <c>GIT_CONFIG_*</c> variables in effect when <paramref name="arguments"/> were run, or null when none.</summary>
        public IReadOnlyDictionary<string, string>? AmbientEnvironmentFor(string arguments)
            => _ambientEnvironment.GetValueOrDefault(arguments);

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
            _ambientEnvironment[arguments] = GitProcessEnvironmentAmbient.Current.Value;
            // Remote delete needs a network push; short-circuit so the test stays offline.
            if (arguments.Contains("push origin --delete", StringComparison.Ordinal)
                || arguments.Contains("--force-with-lease=", StringComparison.Ordinal)
                || arguments.Contains(" fetch ", StringComparison.Ordinal)
                || arguments.StartsWith("fetch ", StringComparison.Ordinal))
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
