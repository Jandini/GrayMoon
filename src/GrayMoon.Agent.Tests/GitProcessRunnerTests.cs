using System.Threading;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GitProcessRunnerTests
{
    private static GitProcessRunner CreateRunner(int defaultSeconds = 60, int networkSeconds = 180, int gitVersionSeconds = 90, int cloneSeconds = 0)
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        return new GitProcessRunner(
            commandLine,
            Options.Create(new GitProcessOptions
            {
                DefaultTimeoutSeconds = defaultSeconds,
                NetworkTimeoutSeconds = networkSeconds,
                GitVersionTimeoutSeconds = gitVersionSeconds,
                CloneTimeoutSeconds = cloneSeconds,
            }),
            NullLogger<GitProcessRunner>.Instance);
    }

    [Theory]
    [InlineData("fetch origin --prune")]
    [InlineData("pull origin main")]
    [InlineData("push origin main")]
    [InlineData("ls-remote --heads origin")]
    public void ResolveTimeout_StringOverload_UsesNetworkTier_ForNetworkSubcommands(string arguments)
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180);

        var timeout = runner.ResolveTimeout("git", arguments);

        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }

    [Fact]
    public void ResolveTimeout_StringOverload_UsesInfiniteTimeout_ForClone_WhenCloneTimeoutSecondsIsDefault()
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180, cloneSeconds: 0);

        var timeout = runner.ResolveTimeout("git", "clone https://example.com/repo.git dest");

        Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
    }

    [Fact]
    public void ResolveTimeout_StringOverload_UsesConfiguredCloneTimeout_WhenCloneTimeoutSecondsIsSet()
    {
        var runner = CreateRunner(cloneSeconds: 600);

        var timeout = runner.ResolveTimeout("git", "clone https://example.com/repo.git dest");

        Assert.Equal(TimeSpan.FromSeconds(600), timeout);
    }

    [Fact]
    public void ResolveTimeout_ArgumentListOverload_UsesInfiniteTimeout_ForClone_WhenCloneTimeoutSecondsIsDefault()
    {
        var runner = CreateRunner(cloneSeconds: 0);

        var timeout = runner.ResolveTimeout("git", ["clone", "https://example.com/repo.git", "dest"]);

        Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
    }

    [Theory]
    [InlineData("status --porcelain=v2 -z --branch")]
    [InlineData("rev-parse --verify HEAD")]
    [InlineData("commit -F -")]
    [InlineData("config --global --add safe.directory /repo")]
    public void ResolveTimeout_StringOverload_UsesDefaultTier_ForLocalSubcommands(string arguments)
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180);

        var timeout = runner.ResolveTimeout("git", arguments);

        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public void ResolveTimeout_StringOverload_UsesGitVersionTier_ForDotnetGitVersionInvocation()
    {
        var runner = CreateRunner(defaultSeconds: 60, gitVersionSeconds: 90);

        var timeout = runner.ResolveTimeout("dotnet", "gitversion /output json");

        Assert.Equal(TimeSpan.FromSeconds(90), timeout);
    }

    [Fact]
    public void ResolveTimeout_StringOverload_UsesGitVersionTier_ForDotnetGitVersionExecutable()
    {
        var runner = CreateRunner(gitVersionSeconds: 90);

        var timeout = runner.ResolveTimeout("dotnet-gitversion", "/output json");

        Assert.Equal(TimeSpan.FromSeconds(90), timeout);
    }

    [Fact]
    public void ResolveTimeout_ArgumentListOverload_UsesNetworkTier_ForPush()
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180);

        var timeout = runner.ResolveTimeout("git", ["push", "origin", "main"]);

        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }

    [Fact]
    public void ResolveTimeout_ArgumentListOverload_UsesDefaultTier_ForStatus()
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180);

        var timeout = runner.ResolveTimeout("git", ["status", "--porcelain=v2"]);

        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public void ResolveTimeout_NonGitExecutable_UsesDefaultTier()
    {
        var runner = CreateRunner(defaultSeconds: 60, networkSeconds: 180);

        var timeout = runner.ResolveTimeout("dotnet", "restore --force --no-cache");

        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }
}
