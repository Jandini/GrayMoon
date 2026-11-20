using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;

namespace GrayMoon.App.Tests;

public sealed class ConnectorUrlResolverTests
{
    // ---- GitHub ------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/acme/repo")]
    [InlineData("https://github.com/acme/repo.git")]
    [InlineData("git@github.com:acme/repo.git")]
    [InlineData("ssh://git@github.com/acme/repo.git")]
    [InlineData("github.com/acme/repo")]
    [InlineData("https://www.github.com/acme")]
    [InlineData("  https://GitHub.com/acme/repo  ")]
    public void A_github_com_repository_address_becomes_the_public_api(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, input);

        Assert.True(resolution.IsValid);
        Assert.Equal(ConnectorEndpointKind.GitHubCom, resolution.Kind);
        Assert.Equal(["https://api.github.com/"], resolution.Candidates);
        Assert.False(resolution.IsExact);
    }

    [Theory]
    [InlineData("https://api.github.com/")]
    [InlineData("https://api.github.com")]
    [InlineData("https://ghe.example.com/api/v3")]
    [InlineData("https://GHE.example.com/api/v3/")]
    [InlineData("https://api.acme.ghe.com/")]
    public void An_address_that_already_is_the_api_is_exact(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, input);

        Assert.True(resolution.IsValid);
        Assert.True(resolution.IsExact);
        Assert.Single(resolution.Candidates);
        // Saved as typed: an existing connector's address is never rewritten.
        Assert.Equal(input.Trim(), ConnectorUrlResolver.AddressToSave(resolution, input, resolution.Candidates[0]));
    }

    [Fact]
    public void A_company_hosted_repository_address_tries_api_v3_first_and_drops_the_repository_path()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, "https://git.company.com/team/repo.git");

        Assert.Equal(ConnectorEndpointKind.GitHubEnterpriseServer, resolution.Kind);
        Assert.Equal(
            ["https://git.company.com/api/v3/", "https://api.git.company.com/", "https://git.company.com/team/repo/"],
            resolution.Candidates);
        Assert.False(resolution.IsExact);
        Assert.Equal("https://git.company.com", resolution.WebRoot);
    }

    [Fact]
    public void A_company_host_keeps_its_scheme_and_port()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, "http://git.company.com:8443/team/repo");

        Assert.Equal("http://git.company.com:8443/api/v3/", resolution.Candidates[0]);
        Assert.Equal("http://git.company.com:8443", resolution.WebRoot);
    }

    [Fact]
    public void An_ssh_clone_address_of_a_company_host_becomes_https_without_the_ssh_port()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, "ssh://git@git.company.com:2222/team/repo.git");

        Assert.Equal("https://git.company.com/api/v3/", resolution.Candidates[0]);
    }

    [Fact]
    public void A_longer_api_v3_address_is_cut_back_to_the_api_root()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, "https://git.company.com/api/v3/repos/team/repo");

        Assert.Equal(["https://git.company.com/api/v3/"], resolution.Candidates);
        Assert.False(resolution.IsExact);
    }

    [Theory]
    [InlineData("https://acme.ghe.com/team/repo")]
    [InlineData("git@acme.ghe.com:team/repo.git")]
    public void A_data_residency_address_uses_the_api_subdomain_without_api_v3(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, input);

        Assert.Equal(ConnectorEndpointKind.GitHubEnterpriseCloud, resolution.Kind);
        Assert.Equal(["https://api.acme.ghe.com/"], resolution.Candidates);
        Assert.Equal("https://acme.ghe.com", resolution.WebRoot);
    }

    [Theory]
    [InlineData(null, ConnectorUrlResolver.RequiredMessage)]
    [InlineData("   ", ConnectorUrlResolver.RequiredMessage)]
    [InlineData("not a url", ConnectorUrlResolver.InvalidMessage)]
    [InlineData("ftp://github.com/acme/repo", ConnectorUrlResolver.InvalidMessage)]
    [InlineData("https://", ConnectorUrlResolver.InvalidMessage)]
    public void Text_that_is_not_an_address_is_refused_with_a_message(string? input, string expected)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.GitHub, input);

        Assert.False(resolution.IsValid);
        Assert.Equal(expected, resolution.Error);
        Assert.Empty(resolution.Candidates);
    }

    // ---- NuGet -------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/acme/repo")]
    [InlineData("git@github.com:acme/repo.git")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/orgs/acme/packages")]
    [InlineData("github.com/acme")]
    public void A_github_address_becomes_that_owners_packages_feed(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, input);

        Assert.True(resolution.IsValid);
        Assert.Equal(ConnectorEndpointKind.GitHubPackages, resolution.Kind);
        Assert.Equal("acme", resolution.Owner);
        Assert.Equal(["https://nuget.pkg.github.com/acme/index.json"], resolution.Candidates);
        Assert.False(resolution.IsExact);
    }

    [Theory]
    [InlineData("https://nuget.pkg.github.com/acme/")]
    [InlineData("https://nuget.pkg.github.com/acme/index.json")]
    public void A_packages_feed_address_with_an_owner_is_exact(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, input);

        Assert.True(resolution.IsExact);
        Assert.Equal("acme", resolution.Owner);
        Assert.Equal(["https://nuget.pkg.github.com/acme/index.json"], resolution.Candidates);
    }

    [Theory]
    [InlineData("https://nuget.pkg.github.com/")]
    [InlineData("https://nuget.pkg.github.com/index.json")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/orgs")]
    public void A_packages_feed_without_an_owner_is_refused_but_still_leads_to_the_token_page(string input)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, input);

        Assert.False(resolution.IsValid);
        Assert.Equal(ConnectorUrlResolver.OwnerRequiredMessage, resolution.Error);
        Assert.Empty(resolution.Candidates);
        Assert.Equal(ConnectorEndpointKind.GitHubPackages, resolution.Kind);
        Assert.NotNull(ConnectorUrlResolver.GetTokenPageUrl(ConnectorType.NuGet, input));
    }

    [Theory]
    [InlineData("nuget.org", false)]
    [InlineData("https://www.nuget.org/packages/Foo", false)]
    [InlineData("https://api.nuget.org", false)]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    public void A_nuget_org_address_becomes_the_public_index(string input, bool exact)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, input);

        Assert.Equal(ConnectorEndpointKind.NuGetOrg, resolution.Kind);
        Assert.Equal(["https://api.nuget.org/v3/index.json"], resolution.Candidates);
        Assert.Equal(exact, resolution.IsExact);
    }

    [Fact]
    public void Another_feed_tries_the_v3_index_then_the_plain_index()
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, "https://proget.example/nuget/feed/");

        Assert.Equal(ConnectorEndpointKind.NuGetFeed, resolution.Kind);
        Assert.Equal(
            ["https://proget.example/nuget/feed/v3/index.json", "https://proget.example/nuget/feed/index.json"],
            resolution.Candidates);
        Assert.False(resolution.IsExact);
    }

    [Theory]
    [InlineData("https://proget.example/nuget/feed/v3", false)]
    [InlineData("https://proget.example/nuget/feed/v3/index.json", true)]
    public void Another_feed_that_names_its_index_has_one_candidate(string input, bool exact)
    {
        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.NuGet, input);

        Assert.Equal(["https://proget.example/nuget/feed/v3/index.json"], resolution.Candidates);
        Assert.Equal(exact, resolution.IsExact);
    }

    // ---- Token page --------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.github.com/", "https://github.com/settings/tokens/new?scopes=repo,workflow,read:packages&description=GrayMoon%20Token")]
    [InlineData("https://github.com/acme/repo", "https://github.com/settings/tokens/new?scopes=repo,workflow,read:packages&description=GrayMoon%20Token")]
    [InlineData("https://git.company.com/api/v3/", "https://git.company.com/settings/tokens/new?scopes=repo,workflow,read:packages&description=GrayMoon%20Token")]
    [InlineData("https://git.company.com/team/repo", "https://git.company.com/settings/tokens/new?scopes=repo,workflow,read:packages&description=GrayMoon%20Token")]
    [InlineData("https://api.acme.ghe.com/", "https://acme.ghe.com/settings/tokens/new?scopes=repo,workflow,read:packages&description=GrayMoon%20Token")]
    public void The_github_token_page_is_on_the_site_the_address_belongs_to(string input, string expected)
    {
        Assert.Equal(expected, ConnectorUrlResolver.GetTokenPageUrl(ConnectorType.GitHub, input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void No_token_page_without_a_usable_address(string? input)
    {
        Assert.Null(ConnectorUrlResolver.GetTokenPageUrl(ConnectorType.GitHub, input));
        Assert.True(ConnectorUrlResolver.OffersTokenPage(ConnectorType.GitHub, input));
    }

    [Fact]
    public void A_packages_feed_token_only_asks_to_read_packages()
    {
        Assert.Equal(
            "https://github.com/settings/tokens/new?scopes=read:packages&description=GrayMoon%20Token",
            ConnectorUrlResolver.GetTokenPageUrl(ConnectorType.NuGet, "https://nuget.pkg.github.com/acme/"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://nuget.pkg.github.com/acme/", true)]
    [InlineData("https://github.com/acme/repo", true)]
    [InlineData("https://api.nuget.org/v3/index.json", false)]
    [InlineData("https://proget.example/nuget/feed", false)]
    public void Create_is_offered_for_a_nuget_connector_only_when_it_can_be_github_packages(string input, bool expected)
    {
        Assert.Equal(expected, ConnectorUrlResolver.OffersTokenPage(ConnectorType.NuGet, input));
    }

    // ---- What the dialog fills in ------------------------------------------------------------

    [Fact]
    public void An_untouched_name_follows_the_type_and_then_the_packages_owner()
    {
        var form = new ConnectorForm();
        var autoName = form.ConnectorName;

        form.ConnectorType = ConnectorType.NuGet;
        autoName = ConnectorFormAutoFill.ApplyName(form, autoName, ConnectorUrlResolver.DefaultName(ConnectorType.NuGet));
        Assert.Equal("NuGet", form.ConnectorName);

        autoName = ConnectorFormAutoFill.ApplyName(form, autoName, ConnectorUrlResolver.DefaultName(ConnectorType.NuGet, "acme"));
        Assert.Equal("NuGet acme", form.ConnectorName);

        form.ConnectorType = ConnectorType.GitHub;
        ConnectorFormAutoFill.ApplyName(form, autoName, ConnectorUrlResolver.DefaultName(ConnectorType.GitHub));
        Assert.Equal("GitHub", form.ConnectorName);
    }

    [Fact]
    public void A_name_the_user_typed_is_never_replaced()
    {
        var form = new ConnectorForm();
        var autoName = form.ConnectorName;
        form.ConnectorName = "Work GitHub";

        var owned = ConnectorFormAutoFill.ApplyName(form, autoName, "NuGet");

        Assert.Equal("Work GitHub", form.ConnectorName);
        Assert.Equal(autoName, owned);
    }

    [Fact]
    public void Edit_mode_never_renames()
    {
        var form = new ConnectorForm { ConnectorName = "GitHub" };

        Assert.Null(ConnectorFormAutoFill.ApplyName(form, autoName: null, "NuGet"));
        Assert.Equal("GitHub", form.ConnectorName);
    }

    [Fact]
    public void Switching_to_nuget_clears_the_default_address_instead_of_filling_in_a_feed_without_an_owner()
    {
        var form = new ConnectorForm { ConnectorType = ConnectorType.NuGet };

        ConnectorFormAutoFill.ApplyTypeChange(form, ConnectorType.GitHub);

        Assert.Equal(string.Empty, form.ApiBaseUrl);
    }

    [Fact]
    public void Switching_back_to_github_fills_in_a_blank_address()
    {
        var form = new ConnectorForm { ConnectorType = ConnectorType.GitHub, ApiBaseUrl = " " };

        ConnectorFormAutoFill.ApplyTypeChange(form, ConnectorType.NuGet);

        Assert.Equal("https://api.github.com/", form.ApiBaseUrl);
    }

    [Theory]
    [InlineData(ConnectorType.NuGet, ConnectorType.GitHub, "https://github.com/acme/repo")]
    [InlineData(ConnectorType.NuGet, ConnectorType.GitHub, "https://ghe.example.com/api/v3")]
    [InlineData(ConnectorType.GitHub, ConnectorType.NuGet, "https://nuget.pkg.github.com/acme/")]
    public void An_address_the_user_chose_survives_a_type_switch(ConnectorType newType, ConnectorType previousType, string address)
    {
        var form = new ConnectorForm { ConnectorType = newType, ApiBaseUrl = address, UserToken = "token" };

        ConnectorFormAutoFill.ApplyTypeChange(form, previousType);

        Assert.Equal(address, form.ApiBaseUrl);
        Assert.Equal("token", form.UserToken);
    }
}
