using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Tests;

public sealed class GitIgnoredPathFilterTests
{
    [Fact]
    public void IsIgnoredPathsAddError_matches_git_stderr()
    {
        Assert.True(GitIgnoredPathFilter.IsIgnoredPathsAddError(
            "The following paths are ignored by one of your .gitignore files:\n.work",
            null));
    }

    [Fact]
    public void IsIgnoredPathsAddError_ignores_unrelated_failures()
    {
        Assert.False(GitIgnoredPathFilter.IsIgnoredPathsAddError("index.lock", "fatal: Unable to create index.lock"));
        Assert.False(GitIgnoredPathFilter.IsIgnoredPathsAddError(null, null));
    }
}
