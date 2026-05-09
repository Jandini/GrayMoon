namespace GrayMoon.App.Services;

/// <summary>Converts git clone URLs to GitHub web URLs for links.</summary>
public static class RepositoryUrlHelper
{
    /// <summary>
    /// Builds the browser URL for a workflow run. Prefer the clone URL host (GitHub.com or Enterprise)
    /// plus <c>owner/repo/actions/runs/{runId}</c> so links match the repo and avoid bad or relative <c>html_url</c> from the API.
    /// </summary>
    public static string? GetWorkflowRunWebUrl(string? cloneUrl, string? owner, string? repositoryName, long runId, string? connectorApiBaseUrl = null)
    {
        if (runId <= 0 || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName))
            return null;

        var root = TryGetGitHostRootFromCloneUrl(cloneUrl, out var hostRoot)
            ? hostRoot
            : GetWebRootFromConnectorApiBase(connectorApiBaseUrl);
        if (string.IsNullOrWhiteSpace(root))
            return null;

        root = root.TrimEnd('/');
        return $"{root}/{owner}/{repositoryName}/actions/runs/{runId}";
    }

    /// <summary>HTTPS origin (scheme + host, no path) from a git remote URL, when parseable.</summary>
    public static bool TryGetGitHostRootFromCloneUrl(string? cloneUrl, out string? root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return false;

        var u = cloneUrl.Trim();
        if (u.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = u.IndexOf(':');
            if (colon <= "git@".Length)
                return false;
            var sshHost = u["git@".Length..colon];
            if (string.IsNullOrWhiteSpace(sshHost))
                return false;
            root = $"https://{sshHost}";
            return true;
        }

        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        if (string.IsNullOrEmpty(uri.Host))
            return false;

        root = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }

    /// <summary>Web UI root from connector API base (GitHub.com or GHES <c>.../api/v3</c>).</summary>
    public static string? GetWebRootFromConnectorApiBase(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return "https://github.com";

        var baseTrim = apiBaseUrl.Trim().TrimEnd('/');
        if (baseTrim.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            return "https://github.com";

        const string suffix = "/api/v3";
        if (baseTrim.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return baseTrim[..^suffix.Length];

        var idx = baseTrim.IndexOf("/api/v3/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return baseTrim[..idx];

        return baseTrim;
    }

    /// <summary>Converts a repository's CloneUrl to a GitHub web URL, or null if not a GitHub repo.</summary>
    public static string? GetRepositoryUrl(string? cloneUrl)
    {
        if (string.IsNullOrEmpty(cloneUrl))
            return null;

        var url = cloneUrl.Trim();

        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            url = url.Replace("git@github.com:", "https://github.com/", StringComparison.OrdinalIgnoreCase);
        else if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        { }
        else
            return null;

        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        return url;
    }

    /// <summary>Parses owner and repo from a GitHub clone URL. Returns true if the URL is a recognized GitHub URL.</summary>
    public static bool TryParseGitHubOwnerRepo(string? cloneUrl, out string? owner, out string? repo)
    {
        owner = null;
        repo = null;
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return false;

        var url = cloneUrl.Trim();
        string path;
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            path = url["git@github.com:".Length..];
        else if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            path = url["https://github.com/".Length..];
        else if (url.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            path = url["http://github.com/".Length..];
        else
            return false;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }
}
