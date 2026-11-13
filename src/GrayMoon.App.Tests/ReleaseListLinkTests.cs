using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class ReleaseListLinkTests
{
    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0+abcdef")]
    [InlineData("1.0.0.0")]
    [InlineData(null)]
    [InlineData("")]
    public void Release_version_filters_the_releases_page_to_release(string? version)
    {
        Assert.Equal(
            "https://github.com/Jandini/GrayMoon.Release/releases?q=release&expanded=true",
            ReleaseListLink.ForVersion(version));
        Assert.Null(ReleaseListLink.PrereleaseBranch(version));
    }

    [Theory]
    [InlineData("0.2.0-main.12", "main")]
    [InlineData("0.1.0-desktop.23", "desktop")]
    [InlineData("0.2.0-accept-prerelease-updates.4", "accept-prerelease-updates")]
    [InlineData("0.2.0-feature-foo.3", "feature-foo")]
    [InlineData("0.2.0-Feedback.5", "Feedback")]
    [InlineData("0.2.0-main", "main")]
    [InlineData("v0.2.0-main.12+sha.1234", "main")]
    public void Prerelease_version_filters_the_releases_page_to_its_branch(string version, string branch)
    {
        Assert.Equal(branch, ReleaseListLink.PrereleaseBranch(version));
        Assert.Equal(
            $"https://github.com/Jandini/GrayMoon.Release/releases?q={Uri.EscapeDataString(branch)}&expanded=true",
            ReleaseListLink.ForVersion(version));
    }
}
