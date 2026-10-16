using System.Text;
using System.Text.RegularExpressions;

namespace GrayMoon.Common.Git;

/// <summary>
/// Wire values for <see cref="GitRemoteFailure.Kind"/>. Additive and optional on worker payloads.
/// </summary>
public static class GitRemoteFailureKind
{
    public const string SsoAuthorizationRequired = "ssoAuthorizationRequired";
    public const string AuthenticationFailed = "authenticationFailed";
    public const string AccessDenied = "accessDenied";
    public const string RepositoryNotFound = "repositoryNotFound";
    public const string NetworkFailure = "networkFailure";
    public const string Unknown = "unknown";
}

/// <summary>
/// One classified remote-git failure. <see cref="DisplayMessage"/> and the labels are safe to show and store.
/// <see cref="AuthorizationUrl"/> is the exact validated GitHub URL, or null, and must not be copied into
/// <see cref="DisplayMessage"/>, logs, or database fields.
/// </summary>
public sealed record GitRemoteFailure(
    int ExitCode,
    string Kind,
    string DisplayMessage,
    string? AuthorizationUrl,
    string? OAuthApplication,
    string? Organization);

/// <summary>Result of <c>git clone</c>. Success carries no failure fields.</summary>
public sealed record GitCloneResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }
    public string? FailureKind { get; init; }
    public string? AuthorizationUrl { get; init; }
    public string? OAuthApplication { get; init; }
    public string? Organization { get; init; }

    public static GitCloneResult Succeeded(int exitCode = 0) => new() { Success = true, ExitCode = exitCode };

    public static GitCloneResult FromFailure(GitRemoteFailure failure) => new()
    {
        Success = false,
        ExitCode = failure.ExitCode,
        ErrorMessage = failure.DisplayMessage,
        FailureKind = failure.Kind,
        AuthorizationUrl = failure.AuthorizationUrl,
        OAuthApplication = failure.OAuthApplication,
        Organization = failure.Organization,
    };
}

/// <summary>
/// Classifies GitHub SAML SSO and other clone failures from git output, and redacts authorization-request
/// query values so they cannot be logged or shown.
/// </summary>
public static partial class GitHubSsoAuthorization
{
    public const string Heading = "GitHub SSO authorization required";

    public static bool IsSsoRequired(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var text = Normalize(output);
        if (text.Contains("enabled or enforced SAML SSO", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Contains(Heading, StringComparison.OrdinalIgnoreCase))
            return true;

        var reauthorize = text.Contains("re-authorize the OAuth Application", StringComparison.OrdinalIgnoreCase)
            || text.Contains("reauthorize the OAuth Application", StringComparison.OrdinalIgnoreCase);
        if (!reauthorize)
            return false;

        return text.Contains("SAML SSO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("github.com/enterprises/", StringComparison.OrdinalIgnoreCase)
            || text.Contains("github.com/orgs/", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/sso?", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorization_request", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a safe failure from git output. <paramref name="validatedAuthorizationUrl"/> is used only when it
    /// passes <see cref="TryValidateAuthorizationUrl"/>; the method never copies a URL into <see cref="GitRemoteFailure.DisplayMessage"/>.
    /// </summary>
    public static GitRemoteFailure Describe(int exitCode, string? stdout, string? stderr, string? validatedAuthorizationUrl = null)
    {
        var text = Normalize(string.Concat(stdout, "\n", stderr));
        if (IsSsoRequired(text))
        {
            var app = CleanLabel(MatchGroup(OAuthApplicationRegex(), text));
            var org = CleanLabel(MatchGroup(OrganizationRegex(), text));
            var url = TryValidateAuthorizationUrl(validatedAuthorizationUrl) ?? TryExtractAuthorizationUrl(stdout, stderr);
            return new GitRemoteFailure(exitCode, GitRemoteFailureKind.SsoAuthorizationRequired, BuildSsoDisplayMessage(app, org), url, app, org);
        }

        if (IsCertificateFailure(text))
            return new GitRemoteFailure(exitCode, GitRemoteFailureKind.Unknown, "Git could not verify the remote certificate.", null, null, null);

        if (IsRepositoryNotFound(text))
            return new GitRemoteFailure(
                exitCode,
                GitRemoteFailureKind.RepositoryNotFound,
                "Git could not find or access this repository. It may be missing, renamed, or hidden by permissions.",
                null, null, null);

        if (IsAuthenticationFailure(text))
            return new GitRemoteFailure(
                exitCode,
                GitRemoteFailureKind.AuthenticationFailed,
                "Git authentication failed. The credentials used to reach this repository were rejected or are missing.",
                null, null, null);

        if (IsAccessDenied(text))
            return new GitRemoteFailure(
                exitCode,
                GitRemoteFailureKind.AccessDenied,
                "Git could not access this repository (HTTP 403).",
                null, null, null);

        if (GitRetryClassifier.IsRetryable(exitCode, stdout, stderr))
            return new GitRemoteFailure(exitCode, GitRemoteFailureKind.NetworkFailure, "Git could not reach the remote. Check the network and retry.", null, null, null);

        var detail = ShortDetail(text);
        var unknown = string.IsNullOrEmpty(detail) ? "Git clone failed." : "Git clone failed. " + detail;
        return new GitRemoteFailure(exitCode, GitRemoteFailureKind.Unknown, unknown, null, null, null);
    }

    public static string BuildSsoExplanation(string? application, string? organization)
    {
        var app = CleanLabel(application);
        var org = CleanLabel(organization);
        if (app != null && org != null)
            return $"Reauthorize {app} to access {org}. Complete authorization in your browser, then retry cloning.";
        if (org != null)
            return $"Reauthorize access to {org}. Complete authorization in your browser, then retry cloning.";
        if (app != null)
            return $"Reauthorize {app}. Complete authorization in your browser, then retry cloning.";
        return "Complete authorization in your browser, then retry cloning.";
    }

    public static string? CleanLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (char.IsControl(c))
                continue;
            builder.Append(c);
            if (builder.Length >= 80)
                break;
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Returns the original absolute URL when it is an approved GitHub SSO route, otherwise null.</summary>
    public static string? TryValidateAuthorizationUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var text = candidate.Trim();
        if (text.Length > 2048)
            return null;

        foreach (var c in text)
        {
            if (char.IsControl(c) || c == '\\' || char.IsWhiteSpace(c))
                return null;
        }

        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (text.Contains('@', StringComparison.Ordinal))
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;
        if (!uri.IsDefaultPort)
            return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!IsSsoPath(uri))
            return null;

        return text;
    }

    public static string? TryExtractAuthorizationUrl(string? stdout, string? stderr)
    {
        var text = string.Concat(stdout, "\n", stderr);
        foreach (Match match in HttpsUrlRegex().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
            var valid = TryValidateAuthorizationUrl(candidate);
            if (valid != null)
                return valid;
        }

        return null;
    }

    /// <summary>Removes authorization-request query values. Safe on null. Leaves text unchanged when the marker is absent.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.IndexOf("authorization_request", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        var withoutUrls = SsoUrlRegex().Replace(text, "https://github.com/sso-authorization-redacted");
        return AuthorizationRequestRegex().Replace(withoutUrls, "authorization_request=redacted");
    }

    private static string BuildSsoDisplayMessage(string? application, string? organization)
        => Heading + ". " + BuildSsoExplanation(application, organization);

    private static bool IsCertificateFailure(string text)
        => text.Contains("SSL certificate problem", StringComparison.OrdinalIgnoreCase)
            || text.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable to get local issuer certificate", StringComparison.OrdinalIgnoreCase);

    private static bool IsRepositoryNotFound(string text)
        => text.Contains("repository not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("returned error: 404", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP code = 404", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthenticationFailure(string text)
        => text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid username or password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not read username", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not read password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("returned error: 401", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP code = 401", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessDenied(string text)
        => text.Contains("returned error: 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP code = 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("permission to", StringComparison.OrdinalIgnoreCase) && text.Contains("denied", StringComparison.OrdinalIgnoreCase);

    private static bool IsSsoPath(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || !segments[2].Equals("sso", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!segments[0].Equals("enterprises", StringComparison.OrdinalIgnoreCase)
            && !segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsSafeSlug(segments[1]);
    }

    private static bool IsSafeSlug(string slug)
    {
        if (slug.Length == 0 || slug.Length > 200 || slug.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in slug)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                continue;
            return false;
        }

        return true;
    }

    private static string ShortDetail(string text)
    {
        var redacted = Redact(text) ?? "";
        foreach (var raw in redacted.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
                line = line["remote:".Length..].Trim();
            if (line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                line = line["fatal:".Length..].Trim();
            if (line.StartsWith("Cloning into", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length == 0 || line.Contains("authorization_request", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Contains("sso-authorization-redacted", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length > 240)
                line = line[..240] + "...";
            return line;
        }

        return "";
    }

    private static string? MatchGroup(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Normalize(string text)
    {
        var stripped = AnsiRegex().Replace(text, "");
        return stripped.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"OAuth Application\s+'([^'\r\n]+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OAuthApplicationRegex();

    [GeneratedRegex(@"The '([^'\r\n]+)' organization has enabled or enforced SAML SSO", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrganizationRegex();

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpsUrlRegex();

    [GeneratedRegex(@"https://[^\s<>""']*authorization_request=[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SsoUrlRegex();

    [GeneratedRegex(@"authorization_request=[^\s&<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRequestRegex();
}
