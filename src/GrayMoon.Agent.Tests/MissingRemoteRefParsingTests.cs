using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Tests;

/// <summary>
/// A branch whose remote counterpart has been deleted keeps its upstream in git config, so a ref-limited
/// fetch still asks for it and git fails the whole command. Recognising that message is what lets the minimal
/// fetch drop the dead ref and carry on instead of reporting an error on the repository row.
/// </summary>
public sealed class MissingRemoteRefParsingTests
{
    [Fact]
    public void Reads_the_ref_name_from_the_git_message()
    {
        var missing = GitService.ParseMissingRemoteRefs("fatal: couldn't find remote ref demo");

        Assert.Equal(["demo"], missing);
    }

    [Fact]
    public void Reads_every_missing_ref_once_and_strips_the_refs_heads_prefix()
    {
        var output = string.Join('\n',
        [
            "fatal: couldn't find remote ref refs/heads/demo",
            "fatal: couldn't find remote ref peter-demo",
            "fatal: couldn't find remote ref demo",
        ]);

        var missing = GitService.ParseMissingRemoteRefs(output);

        Assert.Equal(["demo", "peter-demo"], missing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/api.git/'")]
    public void Reports_nothing_for_unrelated_output(string? output)
    {
        Assert.Empty(GitService.ParseMissingRemoteRefs(output));
    }
}
