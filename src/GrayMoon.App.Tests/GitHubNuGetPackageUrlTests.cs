using GrayMoon.App.Services.GitHub;

namespace GrayMoon.App.Tests;

public class GitHubNuGetPackageUrlTests
{
    [Fact]
    public void GitHub_packages_registry_links_to_the_repository_package_page()
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageUrl(
            "https://nuget.pkg.github.com/Jandini/index.json",
            "GrayMoon.App",
            "https://github.com/Jandini/GrayMoon.git");

        Assert.Equal("https://github.com/Jandini/GrayMoon/pkgs/nuget/GrayMoon.App", url);
    }

    [Fact]
    public void Ssh_clone_url_uses_https_and_keeps_an_unusual_package_id()
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageUrl(
            "https://nuget.pkg.github.com/acme/",
            "Acme.Lib_with space",
            "git@github.com:acme/lib.git");

        Assert.Equal("https://github.com/acme/lib/pkgs/nuget/Acme.Lib_with%20space", url);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("https://proget.example/nuget/feed")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_registries_are_not_links(string? apiBaseUrl)
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageUrl(
            apiBaseUrl,
            "GrayMoon.App",
            "https://github.com/Jandini/GrayMoon.git");

        Assert.Null(url);
    }

    [Fact]
    public void Missing_repository_is_not_a_link()
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageUrl(
            "https://nuget.pkg.github.com/Jandini/index.json",
            "GrayMoon.App",
            cloneUrl: null);

        Assert.Null(url);
    }

    [Fact]
    public void Settings_url_points_at_the_owner_package_settings_page()
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageSettingsUrl(
            "https://nuget.pkg.github.com/Jandini/index.json",
            "GrayMoon.App",
            "git@github.com:Jandini/GrayMoon.git");

        Assert.Equal("https://github.com/orgs/Jandini/packages/nuget/GrayMoon.App/settings", url);
    }

    [Fact]
    public void Settings_url_is_null_for_other_registries()
    {
        var url = RepositoryUrlHelper.GetGitHubNuGetPackageSettingsUrl(
            "https://api.nuget.org/v3/index.json",
            "GrayMoon.App",
            "https://github.com/Jandini/GrayMoon.git");

        Assert.Null(url);
    }
}
