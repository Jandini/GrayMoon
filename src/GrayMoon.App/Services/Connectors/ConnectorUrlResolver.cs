using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Connectors;

/// <summary>What a connector address points at.</summary>
public enum ConnectorEndpointKind
{
    GitHubCom,
    GitHubEnterpriseCloud,
    GitHubEnterpriseServer,
    NuGetOrg,
    GitHubPackages,
    NuGetFeed,
    ProGet
}

/// <summary>
/// Result of <see cref="ConnectorUrlResolver.Resolve"/>. <see cref="Candidates"/> are the endpoints to try, best first.
/// <see cref="IsExact"/> is true when the text already is the endpoint, so nothing has to be looked up or rewritten.
/// <see cref="Error"/> is a message for the user when the text cannot become an endpoint; <see cref="Kind"/> and
/// <see cref="WebRoot"/> can still be set then (GitHub Packages without an owner).
/// </summary>
public sealed record ConnectorUrlResolution(
    ConnectorEndpointKind? Kind,
    IReadOnlyList<string> Candidates,
    string? Owner,
    bool IsExact,
    string? WebRoot,
    string? Error)
{
    public bool IsValid => Error == null;

    /// <summary>ProGet only: the server address with a trailing slash, without the feed path.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>ProGet only: the feed name, from the address or from the Feed box; null while it is not known.</summary>
    public string? Feed { get; init; }
}

/// <summary>
/// Turns what a user knows (a repository URL, a clone URL, a host, or the endpoint itself) into the connector's
/// API Base URL candidates. Pure text work, no network: <see cref="ConnectorEndpointTester"/> probes the candidates.
/// </summary>
public static class ConnectorUrlResolver
{
    public const string GitHubApiUrl = "https://api.github.com/";
    public const string NuGetOrgIndexUrl = "https://api.nuget.org/v3/index.json";
    public const string RequiredMessage = "API Base URL is required.";
    public const string InvalidMessage = "Enter a URL, for example https://github.com/owner/repository.";
    public const string OwnerRequiredMessage = "Add the GitHub organization or user that owns the packages, or paste a repository URL.";
    public const string FeedRequiredMessage = "Enter the feed name, or paste a feed URL.";
    public const string FeedInvalidMessage = "A feed name has no spaces or slashes.";

    private const string GitHubHost = "github.com";
    private const string GitHubPackagesHost = "nuget.pkg.github.com";
    private const string GitHubPackagesRoot = "https://nuget.pkg.github.com/";
    private const string IndexJson = "index.json";

    /// <param name="feedName">ProGet only: the Feed box. A feed named in the address wins over it.</param>
    public static ConnectorUrlResolution Resolve(ConnectorType connectorType, string? input, string? feedName = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Invalid(RequiredMessage);
        if (!TryParse(input, out var address))
            return Invalid(InvalidMessage);

        return connectorType switch
        {
            ConnectorType.GitHub => ResolveGitHub(address),
            ConnectorType.NuGet => ResolveNuGet(address),
            ConnectorType.ProGet => ResolveProGet(address, feedName),
            _ => Invalid(InvalidMessage)
        };
    }

    /// <summary>The address the connector is saved with: the text as typed when it already is the endpoint, else the candidate.</summary>
    public static string AddressToSave(ConnectorUrlResolution resolution, string input, string candidate)
        => resolution.IsExact ? input.Trim() : candidate;

    /// <summary>
    /// The "new personal access token" page that matches the address, or null when the address does not lead to one
    /// (blank, not a URL, or a NuGet feed that is not GitHub Packages).
    /// </summary>
    public static string? GetTokenPageUrl(ConnectorType connectorType, string? input)
    {
        var resolution = Resolve(connectorType, input);
        if (string.IsNullOrEmpty(resolution.WebRoot))
            return null;

        var scopes = connectorType == ConnectorType.NuGet ? "read:packages" : "repo,workflow,read:packages";
        return $"{resolution.WebRoot}/settings/tokens/new?scopes={scopes}&description=GrayMoon%20Token";
    }

    /// <summary>True when a Create button makes sense: every GitHub connector, and a NuGet connector that is blank or on GitHub Packages.</summary>
    public static bool OffersTokenPage(ConnectorType connectorType, string? input)
    {
        if (connectorType == ConnectorType.GitHub)
            return true;
        if (connectorType != ConnectorType.NuGet)
            return false;
        return string.IsNullOrWhiteSpace(input) || Resolve(connectorType, input).Kind == ConnectorEndpointKind.GitHubPackages;
    }

    /// <summary>
    /// The name a new connector gets until the user types one. A GitHub Packages connector names its owner and a ProGet
    /// connector its feed (passed as <paramref name="owner"/>), because each owner or feed needs its own connector.
    /// </summary>
    public static string DefaultName(ConnectorType connectorType, string? owner = null)
        => connectorType switch
        {
            ConnectorType.NuGet => string.IsNullOrWhiteSpace(owner) ? "NuGet" : $"NuGet {owner.Trim()}",
            ConnectorType.ProGet => string.IsNullOrWhiteSpace(owner) ? "ProGet" : $"ProGet {owner.Trim()}",
            _ => "GitHub"
        };

    /// <summary>The address a new connector of this type starts with. NuGet starts blank: a GitHub Packages feed needs its owner.</summary>
    public static string DefaultUrl(ConnectorType connectorType)
        => connectorType == ConnectorType.GitHub ? GitHubApiUrl : string.Empty;

    private static ConnectorUrlResolution ResolveGitHub(Address address)
    {
        if (IsGitHubComHost(address.Host))
        {
            var exact = address.Host == "api.github.com" && address.Segments.Length == 0;
            return new(ConnectorEndpointKind.GitHubCom, [GitHubApiUrl], null, exact, "https://github.com", null);
        }

        if (address.Host.EndsWith(".ghe.com", StringComparison.Ordinal))
        {
            var isApiHost = address.Host.StartsWith("api.", StringComparison.Ordinal);
            var tenant = isApiHost ? address.Host["api.".Length..] : address.Host;
            return new(
                ConnectorEndpointKind.GitHubEnterpriseCloud,
                [$"https://api.{tenant}/"],
                null,
                isApiHost && address.Segments.Length == 0,
                $"https://{tenant}",
                null);
        }

        // Any other host is treated as GitHub Enterprise Server, whose REST API lives under /api/v3.
        var apiIndex = IndexOfApiV3(address.Segments);
        if (apiIndex >= 0)
        {
            var prefix = string.Join('/', address.Segments[..(apiIndex + 2)]);
            return new(
                ConnectorEndpointKind.GitHubEnterpriseServer,
                [$"{address.Root}/{prefix}/"],
                null,
                apiIndex + 2 == address.Segments.Length,
                address.Root,
                null);
        }

        var candidates = new List<string> { $"{address.Root}/api/v3/" };
        if (!address.Host.StartsWith("api.", StringComparison.Ordinal) && Uri.CheckHostName(address.Host) == UriHostNameType.Dns)
            AddDistinct(candidates, $"{address.Scheme}://api.{address.Authority}/");
        AddDistinct(candidates, address.Segments.Length == 0 ? $"{address.Root}/" : $"{address.Root}/{string.Join('/', address.Segments)}/");

        return new(ConnectorEndpointKind.GitHubEnterpriseServer, candidates, null, false, address.Root, null);
    }

    private static ConnectorUrlResolution ResolveNuGet(Address address)
    {
        if (address.Host == GitHubPackagesHost)
        {
            var owner = address.Segments.FirstOrDefault(s => !s.Equals(IndexJson, StringComparison.OrdinalIgnoreCase));
            return GitHubPackages(owner, isExact: true);
        }

        if (IsGitHubComHost(address.Host))
        {
            var segments = address.Segments;
            var owner = segments.Length == 0
                ? null
                : segments[0] is "orgs" or "users" ? segments.ElementAtOrDefault(1) : segments[0];
            return GitHubPackages(owner, isExact: false);
        }

        if (address.Host == "nuget.org" || address.Host.EndsWith(".nuget.org", StringComparison.Ordinal))
        {
            var exact = address.Host == "api.nuget.org"
                && address.Segments.Length > 0
                && address.Segments[^1].Equals(IndexJson, StringComparison.OrdinalIgnoreCase);
            return new(ConnectorEndpointKind.NuGetOrg, [NuGetOrgIndexUrl], null, exact, null, null);
        }

        var full = address.Segments.Length == 0 ? address.Root : $"{address.Root}/{string.Join('/', address.Segments)}";
        if (address.Segments.Length > 0 && address.Segments[^1].Equals(IndexJson, StringComparison.OrdinalIgnoreCase))
            return new(ConnectorEndpointKind.NuGetFeed, [full], null, true, null, null);
        if (address.Segments.Length > 0 && address.Segments[^1].Equals("v3", StringComparison.OrdinalIgnoreCase))
            return new(ConnectorEndpointKind.NuGetFeed, [$"{full}/{IndexJson}"], null, false, null, null);

        return new(ConnectorEndpointKind.NuGetFeed, [$"{full}/v3/{IndexJson}", $"{full}/{IndexJson}"], null, false, null, null);
    }

    /// <summary>
    /// A ProGet NuGet feed is at <c>SERVER/nuget/FEED/v3/index.json</c>. The feed is read from an API address
    /// (<c>/nuget/FEED/...</c>) or a feed page (<c>/feeds/FEED/...</c>), whichever marker comes first; anything in
    /// front of the marker is kept as the server's base path. Without a marker the address is the server itself.
    /// </summary>
    private static ConnectorUrlResolution ResolveProGet(Address address, string? feedName)
    {
        var segments = address.Segments;
        var marker = Array.FindIndex(segments, s => s.Equals("nuget", StringComparison.OrdinalIgnoreCase) || s.Equals("feeds", StringComparison.OrdinalIgnoreCase));
        var hasFeedInAddress = marker >= 0 && marker + 1 < segments.Length;

        var basePath = hasFeedInAddress && marker > 0 ? "/" + string.Join('/', segments[..marker]) : string.Empty;
        var serverUrl = $"{address.Root}{basePath}/";
        var feed = hasFeedInAddress ? Uri.UnescapeDataString(segments[marker + 1]) : feedName?.Trim();

        if (string.IsNullOrEmpty(feed))
            return new(ConnectorEndpointKind.ProGet, [], null, false, null, null) { ServerUrl = serverUrl };
        if (feed.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\'))
            return new(ConnectorEndpointKind.ProGet, [], null, false, null, FeedInvalidMessage) { ServerUrl = serverUrl };

        return new(
            ConnectorEndpointKind.ProGet,
            [$"{serverUrl}nuget/{Uri.EscapeDataString(feed)}/v3/{IndexJson}"],
            null,
            false,
            null,
            null)
        {
            ServerUrl = serverUrl,
            Feed = feed
        };
    }

    private static ConnectorUrlResolution GitHubPackages(string? owner, bool isExact)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return new(ConnectorEndpointKind.GitHubPackages, [], null, false, "https://github.com", OwnerRequiredMessage);

        return new(
            ConnectorEndpointKind.GitHubPackages,
            [$"{GitHubPackagesRoot}{owner}/{IndexJson}"],
            owner,
            isExact,
            "https://github.com",
            null);
    }

    private static ConnectorUrlResolution Invalid(string error) => new(null, [], null, false, null, error);

    private static bool IsGitHubComHost(string host)
        => host == GitHubHost || host.EndsWith("." + GitHubHost, StringComparison.Ordinal);

    private static int IndexOfApiV3(string[] segments)
    {
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("api", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("v3", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static void AddDistinct(List<string> candidates, string candidate)
    {
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            candidates.Add(candidate);
    }

    /// <summary>
    /// Reads an http(s) URL, a URL without a scheme (<c>github.com/owner/repo</c>), an SSH URL or the SCP form
    /// (<c>git@host:owner/repo.git</c>). Sign-in details in the text are dropped; SSH forms become https.
    /// </summary>
    private static bool TryParse(string input, out Address address)
    {
        address = default;
        var text = input.Trim();
        if (text.Any(char.IsWhiteSpace))
            return false;

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0 && text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = text.IndexOf(':');
            if (colon <= "git@".Length)
                return false;
            text = $"https://{text["git@".Length..colon]}/{text[(colon + 1)..]}";
        }
        else if (schemeEnd < 0)
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;

        var isWeb = uri.Scheme is "http" or "https";
        if (!isWeb && uri.Scheme is not ("ssh" or "git"))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host["www.".Length..];
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;

        // An SSH port says nothing about the web port, so only an http(s) URL keeps its port.
        var scheme = isWeb ? uri.Scheme : "https";
        var authority = isWeb && !uri.IsDefaultPort ? $"{host}:{uri.Port}" : host;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segments[^1] = segments[^1][..^".git".Length];
            if (segments[^1].Length == 0)
                segments = segments[..^1];
        }

        address = new Address(scheme, host, authority, segments);
        return true;
    }

    private readonly record struct Address(string Scheme, string Host, string Authority, string[] Segments)
    {
        public string Root => $"{Scheme}://{Authority}";
    }
}
