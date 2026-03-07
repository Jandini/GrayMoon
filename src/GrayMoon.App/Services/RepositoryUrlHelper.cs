namespace GrayMoon.App.Services;

/// <summary>Converts git clone URLs to GitHub web URLs for links.</summary>
public static class RepositoryUrlHelper
{
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
