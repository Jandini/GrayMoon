using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A failed GitVersion run prints log lines and a stack trace. The row of a repository used to show all of it, cut
/// at 500 characters, for a repository that simply had no commits. The reason is now one sentence.
/// </summary>
public sealed class GitVersionFailureTextTests
{
    private const string NoCommitsOutput =
        "INFO [11/21/26 22:19:17:50] Applicable build agent found: 'LocalBuild'.\r\n" +
        "ERROR [11/21/26 22:19:17:59] An unexpected error occurred:\r\n" +
        "GitVersion.GitVersionException: No commits found on the current branch.\r\n" +
        "   at GitVersion.VersionCalculation.BaseVersionCalculator.GetBaseVersion() in D:\\a\\GitVersion\\GitVersion\\src\\GitVersion.Core\\VersionCalculation\\BaseVersionCalculator.cs:line 35\r\n" +
        "   at GitVersion.VersionCalculation.NextVersionCalculator.FindVersion()\r\n";

    [Theory]
    [InlineData(NoCommitsOutput, null)]
    [InlineData(null, NoCommitsOutput)]
    public void A_repository_without_commits_is_named_in_one_sentence(string? stderr, string? stdout)
    {
        Assert.True(GitVersionFailureText.IsNoCommits(stderr, stdout));
        Assert.Equal(GitVersionFailureText.NoCommits, GitVersionFailureText.Summarize(stderr, stdout));
    }

    [Fact]
    public void Another_failure_keeps_only_the_message_of_the_exception()
    {
        const string output =
            "INFO [11/21/26 22:19:17:50] Applicable build agent found: 'LocalBuild'.\n" +
            "ERROR [11/21/26 22:19:17:59] An unexpected error occurred:\n" +
            "GitVersion.ConfigurationException: Branch configuration 'feature' is missing a regex.\n" +
            "   at GitVersion.Configuration.ConfigurationProvider.Provide()\n";

        Assert.False(GitVersionFailureText.IsNoCommits(output, null));
        Assert.Equal("GitVersion: Branch configuration 'feature' is missing a regex.", GitVersionFailureText.Summarize(output, null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("'dotnet-gitversion' is not recognized as an internal or external command", null)]
    public void Output_without_an_exception_is_left_to_the_caller(string? stderr, string? stdout)
    {
        Assert.Null(GitVersionFailureText.Summarize(stderr, stdout));
    }
}
