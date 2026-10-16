using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitCloneFailureTests : IDisposable
{
    private const string Secret = "SSOQUERYTOKEN";
    private const string SsoUrl = "https://github.com/enterprises/acme-labs-global/sso?authorization_request=" + Secret;
    private const string SsoStderr =
        "Cloning into 'cifx'...\n" +
        "remote: The 'acme-labs' organization has enabled or enforced SAML SSO.\n" +
        "remote: To access this repository, you must re-authorize the OAuth Application 'Visual Studio'.\n" +
        "remote: To access this repository, visit " + SsoUrl + " and try your request again.\n" +
        "fatal: unable to access 'https://github.com/acme-labs/cifx.git/': The requested URL returned error: 403";

    private readonly string _workspace = Directory.CreateTempSubdirectory("graymoon-clone-fail-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Clone_exit_128_sso_returns_a_safe_failure_and_logs_without_the_secret()
    {
        var logger = new CapturingLogger<GitService>();
        var git = Create(logger, new CommandLineResult(128, "", SsoStderr));

        var result = await git.CloneAsync(_workspace, "https://github.com/acme-labs/cifx.git", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(128, result.ExitCode);
        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, result.FailureKind);
        Assert.Equal(SsoUrl, result.AuthorizationUrl);
        Assert.Equal("Visual Studio", result.OAuthApplication);
        Assert.Equal("acme-labs", result.Organization);
        Assert.DoesNotContain(Secret, result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization_request", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("SSO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Clone_into_exit_128_sso_returns_the_same_safe_failure()
    {
        var logger = new CapturingLogger<GitService>();
        var git = Create(logger, new CommandLineResult(128, "", SsoStderr));

        var result = await git.CloneIntoAsync(_workspace, "https://github.com/acme-labs/cifx.git", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, result.FailureKind);
        Assert.Equal(SsoUrl, result.AuthorizationUrl);
        Assert.DoesNotContain(Secret, result.ErrorMessage, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_clone_stays_successful()
    {
        var git = Create(new CapturingLogger<GitService>(), new CommandLineResult(0, "", ""));

        var result = await git.CloneAsync(_workspace, "https://example.invalid/repo.git", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.AuthorizationUrl);
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_an_auth_failure()
    {
        var git = Create(new CapturingLogger<GitService>(), result: null, cancel: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            git.CloneAsync(_workspace, "https://example.invalid/repo.git", null, CancellationToken.None));
    }

    [Fact]
    public void Auth_hint_prefers_sso_text_and_drops_the_secret()
    {
        var hinted = GitRemoteAuth.WithAuthHint(SsoStderr);

        Assert.NotNull(hinted);
        Assert.Contains("GitHub SSO authorization required", hinted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, hinted, StringComparison.Ordinal);
        Assert.DoesNotContain("connector token", hinted, StringComparison.OrdinalIgnoreCase);
    }

    private GitService Create(CapturingLogger<GitService> logger, CommandLineResult? result, bool cancel = false)
    {
        var commandLine = new ScriptedCloneCommandLine(result, cancel);
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        return new GitService(Options.Create(new WorkerOptions()), logger, runner, reader, new LibGit2SharpGitIgnoreService());
    }

    private sealed class ScriptedCloneCommandLine(CommandLineResult? result, bool cancel) : ICommandLineService
    {
        public Task<CommandLineResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? stdin = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
        {
            if (cancel)
                throw new OperationCanceledException();
            return Task.FromResult(result ?? new CommandLineResult(0, "", ""));
        }

        public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
            => Task.FromResult(result ?? new CommandLineResult(0, "", ""));
    }
}
