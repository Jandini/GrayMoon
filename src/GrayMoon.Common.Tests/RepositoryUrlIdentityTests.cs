using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class RepositoryUrlIdentityTests
{
    [Theory]
    [InlineData("https://github.com/example/Avr.Api.git")]
    [InlineData("https://github.com/example/Avr.Api")]
    [InlineData("git@github.com:example/Avr.Api.git")]
    [InlineData("ssh://git@github.com/example/Avr.Api.git")]
    [InlineData("ssh://github.com/example/Avr.Api")]
    [InlineData("HTTPS://GitHub.com/example/Avr.Api/")]
    [InlineData("https://user@github.com/example/Avr.Api.GIT")]
    [InlineData("  http://github.com/example/Avr.Api  ")]
    public void Equivalent_repository_urls_normalize_to_the_same_value(string url)
    {
        Assert.Equal("https://github.com/example/Avr.Api", RepositoryUrlIdentity.NormalizeRepositoryUrl(url));
        Assert.True(RepositoryUrlIdentity.RepositoryUrlsEqual(url, "https://github.com/example/Avr.Api"));
    }

    [Fact]
    public void Repository_url_compare_ignores_path_case()
    {
        Assert.True(RepositoryUrlIdentity.RepositoryUrlsEqual(
            "https://github.com/example/Avr.Api",
            "https://github.com/example/avr.api"));
    }

    [Fact]
    public void Different_owner_is_not_equal()
    {
        Assert.False(RepositoryUrlIdentity.RepositoryUrlsEqual(
            "https://github.com/example/Avr.Api",
            "https://github.com/other/Avr.Api"));
    }

    [Theory]
    [InlineData("https://GitHub.com/", "https://github.com")]
    [InlineData("https://github.com", "https://github.com")]
    [InlineData("https://ghe.company.com/api/v3", "https://ghe.company.com")]
    [InlineData("https://ghe.company.com/api/v3/", "https://ghe.company.com")]
    [InlineData("HTTPS://GHE.Company.com:443/API/v3", "https://ghe.company.com")]
    public void Connector_urls_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, RepositoryUrlIdentity.NormalizeConnectorUrl(input));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("garbage")]
    [InlineData("ftp://example.com/x")]
    public void Garbage_returns_trimmed_input_and_equal_garbage_is_equal(string garbage)
    {
        var padded = "  " + garbage + "  ";
        Assert.Equal(garbage, RepositoryUrlIdentity.NormalizeRepositoryUrl(padded));
        Assert.Equal(garbage, RepositoryUrlIdentity.NormalizeConnectorUrl(padded));
        Assert.True(RepositoryUrlIdentity.RepositoryUrlsEqual(garbage, padded));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, RepositoryUrlIdentity.NormalizeRepositoryUrl("   "));
        Assert.Equal(string.Empty, RepositoryUrlIdentity.NormalizeConnectorUrl(null));
    }
}
