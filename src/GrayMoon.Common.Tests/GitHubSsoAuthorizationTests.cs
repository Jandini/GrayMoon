using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class GitHubSsoAuthorizationTests
{
    private const string Secret = "SSOQUERYTOKEN";

    private const string SsoUrl =
        "https://github.com/enterprises/acme-labs-global/sso?authorization_request=" + Secret;

    private const string SsoStderr =
        "Cloning into 'cifx'...\n" +
        "remote: The 'acme-labs' organization has enabled or enforced SAML SSO.\n" +
        "remote: To access this repository, you must re-authorize the OAuth Application 'Visual Studio'.\n" +
        "remote: To access this repository, visit " + SsoUrl + " and try your request again.\n" +
        "fatal: unable to access 'https://github.com/acme-labs/cifx.git/': The requested URL returned error: 403";

    [Fact]
    public void Sso_with_a_safe_url_is_actionable_and_the_secret_stays_out_of_the_message()
    {
        var failure = GitHubSsoAuthorization.Describe(128, null, SsoStderr);

        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, failure.Kind);
        Assert.Equal(SsoUrl, failure.AuthorizationUrl);
        Assert.Equal("Visual Studio", failure.OAuthApplication);
        Assert.Equal("acme-labs", failure.Organization);
        Assert.Contains("GitHub SSO authorization required", failure.DisplayMessage, StringComparison.Ordinal);
        Assert.Contains("Visual Studio", failure.DisplayMessage, StringComparison.Ordinal);
        Assert.Contains("acme-labs", failure.DisplayMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failure.DisplayMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization_request", failure.DisplayMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("connector token", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sso_without_a_url_has_no_authorization_action()
    {
        const string stderr =
            "remote: The 'acme-labs' organization has enabled or enforced SAML SSO.\n" +
            "remote: To access this repository, you must re-authorize the OAuth Application 'Visual Studio'.\n" +
            "fatal: The requested URL returned error: 403";

        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, failure.Kind);
        Assert.Null(failure.AuthorizationUrl);
        Assert.Equal("Visual Studio", failure.OAuthApplication);
        Assert.DoesNotContain(Secret, failure.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_403_is_access_denied_not_sso()
    {
        const string stderr = "fatal: unable to access 'https://github.com/acme/repo.git/': The requested URL returned error: 403";
        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Equal(GitRemoteFailureKind.AccessDenied, failure.Kind);
        Assert.Null(failure.AuthorizationUrl);
        Assert.DoesNotContain("SSO", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_token_401_is_authentication_guidance_not_sso()
    {
        const string stderr = "fatal: Authentication failed for 'https://github.com/acme/repo.git/'\nfatal: The requested URL returned error: 401";
        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Equal(GitRemoteFailureKind.AuthenticationFailed, failure.Kind);
        Assert.Contains("authentication failed", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SSO", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Visual Studio", failure.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_not_found_does_not_claim_the_repository_is_gone()
    {
        const string stderr = "remote: Repository not found.\nfatal: The requested URL returned error: 404";
        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Equal(GitRemoteFailureKind.RepositoryNotFound, failure.Kind);
        Assert.DoesNotContain("does not exist", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden by permissions", failure.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_failure_is_reported_and_is_not_retried()
    {
        const string stderr = "fatal: unable to access 'https://github.com/acme/repo.git/': SSL certificate problem: unable to get local issuer certificate";
        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Contains("certificate", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(GitRetryClassifier.IsRetryable(128, null, stderr));
    }

    [Theory]
    [InlineData("https://github.com.evil.tld/enterprises/acme/sso?authorization_request=SSOQUERYTOKEN")]
    [InlineData("https://user:pw@github.com/enterprises/acme/sso?authorization_request=SSOQUERYTOKEN")]
    [InlineData("http://github.com/enterprises/acme/sso?authorization_request=SSOQUERYTOKEN")]
    [InlineData("https://github.com:444/enterprises/acme/sso?authorization_request=SSOQUERYTOKEN")]
    [InlineData("https://github.com/acme/repo")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com/enterprises/acme/sso/extra?authorization_request=SSOQUERYTOKEN")]
    [InlineData("https://github.com\\enterprises\\acme\\sso?authorization_request=SSOQUERYTOKEN")]
    public void Malicious_or_unrelated_urls_are_not_authorization_actions(string url)
    {
        var stderr =
            "remote: The 'acme' organization has enabled or enforced SAML SSO.\n" +
            "remote: visit " + url + "\n";

        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);

        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, failure.Kind);
        Assert.Null(failure.AuthorizationUrl);
        Assert.DoesNotContain(Secret, failure.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Crlf_ansi_and_extra_remote_lines_still_classify()
    {
        var stderr = "\u001b[31mremote: The 'acme-labs' organization has enabled or enforced SAML SSO.\u001b[0m\r\n" +
            "remote: something else\r\n" +
            "remote: To access this repository, you must re-authorize the OAuth Application 'Visual Studio'.\r\n" +
            "remote: visit " + SsoUrl + "\r\n";

        var failure = GitHubSsoAuthorization.Describe(128, "noise", stderr);

        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, failure.Kind);
        Assert.Equal("Visual Studio", failure.OAuthApplication);
        Assert.Equal("acme-labs", failure.Organization);
        Assert.Equal(SsoUrl, failure.AuthorizationUrl);
        Assert.DoesNotContain(Secret, failure.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Reauthorize_text_without_sso_evidence_is_not_sso()
    {
        const string stderr = "remote: you must re-authorize the OAuth Application 'Visual Studio'.";
        Assert.False(GitHubSsoAuthorization.IsSsoRequired(stderr));
        Assert.NotEqual(GitRemoteFailureKind.SsoAuthorizationRequired, GitHubSsoAuthorization.Describe(1, null, stderr).Kind);
    }

    [Fact]
    public void Redaction_removes_the_authorization_request_from_loggable_text()
    {
        var redacted = GitHubSsoAuthorization.Redact(SsoStderr);
        Assert.NotNull(redacted);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, LogSafe.ForLog(SsoStderr), StringComparison.Ordinal);
        Assert.Contains("SAML SSO", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_failure_keeps_a_short_message()
    {
        const string stderr = "fatal: unable to access 'https://github.com/acme/repo.git/': Could not resolve host: github.com";
        var failure = GitHubSsoAuthorization.Describe(128, null, stderr);
        Assert.Equal(GitRemoteFailureKind.NetworkFailure, failure.Kind);
        Assert.Contains("could not reach the remote", failure.DisplayMessage, StringComparison.OrdinalIgnoreCase);
    }
}
