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
}
