using System.Globalization;

namespace GrayMoon.App.Services;

/// <summary>Converts git clone URLs to GitHub web URLs for links.</summary>
public static class RepositoryUrlHelper
{
    /// <summary>
    /// Browser URL for the Actions <strong>workflow</strong> tab (<c>…/actions/workflows/build-app.yml</c>), not a run and not the repo file/blob view.
    /// When <paramref name="workflowPath"/> is set, the segment is the path under <c>…/workflows/</c> (e.g. <c>build-app.yml</c> or <c>ci%2Fnested.yml</c>).
    /// </summary>
    public static string? BuildWorkflowPageUrl(
        string? apiWorkflowHtmlUrl,
        string? cloneUrl,
        string? owner,
        string? repositoryName,
        long workflowId,
        string? workflowPath,
        string? connectorApiBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName))
            return null;

        var root = TryGetGitHostRootFromCloneUrl(cloneUrl, out var hostRoot)
            ? hostRoot
            : GetWebRootFromConnectorApiBase(connectorApiBaseUrl);
        if (string.IsNullOrWhiteSpace(root))
            return null;

        root = root.TrimEnd('/');

        var segment = ActionsWorkflowsTabPathSegment(workflowPath);
        if (!string.IsNullOrWhiteSpace(segment))
            return $"{root}/{owner}/{repositoryName}/actions/workflows/{segment}";

        if (IsActionsWorkflowsTabUrl(apiWorkflowHtmlUrl))
            return apiWorkflowHtmlUrl;

        if (workflowId > 0)
            return $"{root}/{owner}/{repositoryName}/actions/workflows/{workflowId.ToString(CultureInfo.InvariantCulture)}";

        return null;
    }

    /// <summary>True when <paramref name="url"/> is an Actions workflows tab link, not blob/tree.</summary>
    private static bool IsActionsWorkflowsTabUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.Contains("/blob/", StringComparison.Ordinal) || url.Contains("/tree/", StringComparison.Ordinal))
            return false;
        return url.Contains("/actions/workflows/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Path segment after <c>owner/repo/actions/workflows/</c>: YAML name or nested path with <c>/</c> encoded as <c>%2F</c>.</summary>
    private static string? ActionsWorkflowsTabPathSegment(string? workflowPath)
    {
        if (string.IsNullOrWhiteSpace(workflowPath))
            return null;

        var p = workflowPath.Replace('\\', '/').TrimStart('/');
        const string marker = "workflows/";
        var idx = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string relative;
        if (idx >= 0)
            relative = p[(idx + marker.Length)..];
        else
        {
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;
            relative = parts[^1];
        }

        if (string.IsNullOrWhiteSpace(relative))
            return null;

        return Uri.EscapeDataString(relative);
    }

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

    /// <summary>Converts a repository's CloneUrl to a web URL, or null if the URL is not parseable.</summary>
    public static string? GetRepositoryUrl(string? cloneUrl)
    {
        if (string.IsNullOrEmpty(cloneUrl))
            return null;

        var url = cloneUrl.Trim();
        string root;
        string path;

        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = url.IndexOf(':');
            if (colon <= "git@".Length)
                return null;
            var host = url["git@".Length..colon];
            if (string.IsNullOrWhiteSpace(host))
                return null;
            root = $"https://{host}";
            path = url[(colon + 1)..].Trim('/');
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            root = $"{uri.Scheme}://{uri.Authority}";
            path = uri.AbsolutePath.Trim('/');
        }
        else
            return null;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        if (string.IsNullOrWhiteSpace(path))
            return null;

        return $"{root}/{path}";
    }

    /// <summary>Parses owner and repo from a git clone URL. Returns true if the URL is parseable.</summary>
    public static bool TryParseGitHubOwnerRepo(string? cloneUrl, out string? owner, out string? repo)
    {
        owner = null;
        repo = null;
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return false;

        var url = cloneUrl.Trim();
        string path;

        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = url.IndexOf(':');
            if (colon <= "git@".Length)
                return false;
            path = url[(colon + 1)..];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            path = uri.AbsolutePath.TrimStart('/');
        }
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
