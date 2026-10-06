using System.Text.RegularExpressions;

namespace GrayMoon.Common.Git;

/// <summary>
/// Stable identity for connector and repository URLs, so a Workspace definition written on one machine
/// matches the same repository or connector on another regardless of clone protocol, host casing or a
/// trailing <c>.git</c>. Unparsable input is returned trimmed so identical strings still compare equal.
/// </summary>
public static class RepositoryUrlIdentity
{
    private const string ApiV3Suffix = "/api/v3";

    // git@host:owner/repo.git (scp-like syntax, no scheme).
    private static readonly Regex ScpLike = new(
        @"^(?:[^@/\s:]+@)?(?<host>[^@/\s:]+):(?<path>(?!//).+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Scheme and host (lower-case, default port dropped) plus any path with <c>/api/v3</c> and the trailing slash removed.</summary>
    public static string NormalizeConnectorUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return trimmed;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(ApiV3Suffix, StringComparison.OrdinalIgnoreCase))
            path = path[..^ApiV3Suffix.Length];
        path = path.TrimEnd('/');

        return (uri.GetLeftPart(UriPartial.Authority) + path).ToLowerInvariant();
    }

    /// <summary>Canonical <c>https://host/owner/repo</c>: lower-case host, original-case path, no <c>.git</c>, no trailing slash.</summary>
    public static string NormalizeRepositoryUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        string host;
        string path;
        var port = string.Empty;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            var m = ScpLike.Match(trimmed);
            if (!m.Success)
                return trimmed;
            host = m.Groups["host"].Value.ToLowerInvariant();
            path = "/" + m.Groups["path"].Value.TrimStart('/');
        }
        else
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "ssh")
                || string.IsNullOrEmpty(uri.Host))
                return trimmed;

            host = uri.Host.ToLowerInvariant();
            path = uri.AbsolutePath;
            if (!uri.IsDefaultPort && uri.Scheme != "ssh")
                port = ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (path.EndsWith('/'))
            path = path[..^1];
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        return "https://" + host + port + path;
    }

    /// <summary>True when both URLs normalize to the same repository (ordinal, ignoring case).</summary>
    public static bool RepositoryUrlsEqual(string? a, string? b) =>
        string.Equals(NormalizeRepositoryUrl(a), NormalizeRepositoryUrl(b), StringComparison.OrdinalIgnoreCase);
}
