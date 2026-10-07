using System.Reflection;
using System.Text;
using GrayMoon.Common;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitRemoteAuthTests
{
    [Theory]
    [InlineData("git version 2.43.0.windows.1", 2, 43)]
    [InlineData("git version 2.31.0", 2, 31)]
    [InlineData("git version 1.9.5", 1, 9)]
    public void ParseGitVersion_ReadsMajorAndMinor(string output, int major, int minor)
        => Assert.Equal(new Version(major, minor), GitRemoteAuth.ParseGitVersion(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not git")]
    public void ParseGitVersion_ReturnsNull_WhenUnrecognised(string? output)
        => Assert.Null(GitRemoteAuth.ParseGitVersion(output));

    [Theory]
    [InlineData(2, 31, true)]
    [InlineData(2, 53, true)]
    [InlineData(2, 30, false)]
    [InlineData(1, 99, false)]
    public void SupportsEnvConfig_RequiresGit231(int major, int minor, bool expected)
        => Assert.Equal(expected, GitRemoteAuth.SupportsEnvConfig(new Version(major, minor)));

    [Fact]
    public void SupportsEnvConfig_IsFalse_WhenVersionUnknown()
        => Assert.False(GitRemoteAuth.SupportsEnvConfig(null));

    [Fact]
    public void BuildEnvironment_KeepsXAccessTokenBasicHeader_AndDisablesCredentialHelpers()
    {
        var env = GitRemoteAuth.BuildEnvironment("tok");

        var expectedHeader = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:tok"));
        Assert.Equal("3", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("core.askpass", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("true", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("credential.helper", env["GIT_CONFIG_KEY_1"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("http.extraHeader", env["GIT_CONFIG_KEY_2"]);
        Assert.Equal(expectedHeader, env["GIT_CONFIG_VALUE_2"]);
    }

    [Fact]
    public void BuildEnvironment_AppendsAfterExistingConfigEntries()
    {
        var env = GitRemoteAuth.BuildEnvironment("tok", existingConfigCount: 2);

        Assert.Equal("5", env["GIT_CONFIG_COUNT"]);
        Assert.False(env.ContainsKey("GIT_CONFIG_KEY_0"));
        Assert.False(env.ContainsKey("GIT_CONFIG_KEY_1"));
        Assert.Equal("core.askpass", env["GIT_CONFIG_KEY_2"]);
        Assert.Equal("http.extraHeader", env["GIT_CONFIG_KEY_4"]);
    }

    [Fact]
    public void BuildArgumentPrefix_MatchesTheLegacyCommandLineForm()
    {
        var prefix = GitRemoteAuth.BuildArgumentPrefix("tok");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:tok"));

        Assert.Equal(
            $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader=Authorization: Basic {base64}\"",
            prefix);
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://github.com/o/r.git/'")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/o/r.git/' not found")]
    [InlineData("fatal: unable to access 'https://x/': The requested URL returned error: 403")]
    [InlineData("remote: Permission to o/r.git denied to someone.")]
    public void IsAuthFailure_RecognisesAuthErrors(string output)
        => Assert.True(GitRemoteAuth.IsAuthFailure(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fatal: couldn't find remote ref demo")]
    [InlineData("CONFLICT (content): Merge conflict in a.txt")]
    [InlineData("error: failed to push some refs (stale info)")]
    public void IsAuthFailure_IgnoresOtherErrors(string? output)
        => Assert.False(GitRemoteAuth.IsAuthFailure(output));

    [Fact]
    public void WithAuthHint_PrefixesHint_OnlyForAuthFailures()
    {
        var authFailure = "fatal: Authentication failed for 'https://x/'";
        var other = "fatal: couldn't find remote ref demo";

        Assert.StartsWith(GitRemoteAuth.AuthFailureHint, GitRemoteAuth.WithAuthHint(authFailure), StringComparison.Ordinal);
        Assert.EndsWith(authFailure, GitRemoteAuth.WithAuthHint(authFailure), StringComparison.Ordinal);
        Assert.Equal(other, GitRemoteAuth.WithAuthHint(other));
        Assert.Null(GitRemoteAuth.WithAuthHint(null));
    }

    [Fact]
    public async Task GitReadsCredentialConfigFromTheAmbientEnvironment()
    {
        // Proves the transport really works against the installed git (skipped on git < 2.31, which uses -c).
        var service = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var version = await service.RunAsync("git", "--version");
        if (!GitRemoteAuth.SupportsEnvConfig(GitRemoteAuth.ParseGitVersion(version.Stdout)))
            return;

        using var _ = new GitProcessEnvironmentScope(GitRemoteAuth.BuildEnvironment("tok", GitRemoteAuth.ReadExistingConfigCount()));
        var header = await service.RunAsync("git", "config --get http.extraHeader");
        var helper = await service.RunAsync("git", "config --show-origin --get-all credential.helper");

        Assert.Equal(0, header.ExitCode);
        Assert.Equal(GitRemoteAuth.BuildAuthHeaderValue("tok"), header.Stdout?.Trim());
        // The empty value resets the helper list: it is the last entry git sees and comes from the command-line scope
        // (stdout is trimmed, so the empty value itself is not visible - only its origin line is).
        var lastEntry = (helper.Stdout ?? "").Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        Assert.StartsWith("command line:", lastEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRemoteGitServiceMethodTakesAnExplicitBearerToken()
    {
        // Guard against a new remote operation silently omitting authentication: each method that talks to
        // the remote must expose the token parameter so callers cannot forget to pass it.
        string[] remoteMethods =
        [
            nameof(IGitService.CloneAsync), nameof(IGitService.CloneIntoAsync), nameof(IGitService.FetchAsync),
            nameof(IGitService.FetchMinimalAsync), nameof(IGitService.PullAsync), nameof(IGitService.PushAsync),
            nameof(IGitService.GetRemoteBranchesAsync), nameof(IGitService.FetchTagsAsync),
            nameof(IGitService.DeleteBranchAsync), nameof(IGitService.ResetToRemoteAsync),
            nameof(IGitService.GetRemoteDefaultBranchAsync), nameof(IGitService.RepairOriginHeadAsync),
        ];

        foreach (var name in remoteMethods)
        {
            var method = typeof(IGitService).GetMethod(name);
            Assert.NotNull(method);
            Assert.Contains(method!.GetParameters(), p => p.Name == "bearerToken");
        }
    }

    [Fact]
    public void GitServiceSourceNeverRunsRemoteSubcommandsOutsideRunRemoteAsync()
    {
        var path = FindGitServiceSource();
        var source = File.ReadAllText(path);

        // Every remote subcommand must be dispatched via runner.RunRemoteAsync (which applies auth).
        var offenders = System.Text.RegularExpressions.Regex.Matches(
                source,
                @"runner\.RunAsync\(\s*""git""\s*,\s*\$?""[^""]*\b(fetch|pull|push|ls-remote|clone)\b")
            .Select(m => m.Value)
            .ToList();

        Assert.True(offenders.Count == 0, "Remote git must go through runner.RunRemoteAsync: " + string.Join(" | ", offenders));
    }

    private static string FindGitServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "GrayMoon.Worker", "Services", "GitService.cs");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "src", "GrayMoon.Worker", "Services", "GitService.cs");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("GitService.cs not found relative to the test output directory.");
    }
}
