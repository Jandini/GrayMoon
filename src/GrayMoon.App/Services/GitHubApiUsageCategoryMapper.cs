namespace GrayMoon.App.Services;

/// <summary>Maps a GitHub REST request path to a coarse category for usage logging/rollup.</summary>
public static class GitHubApiUsageCategoryMapper
{
    public const string Actions = "Actions";
    public const string PullRequests = "PullRequests";
    public const string Reviewers = "Reviewers";
    public const string Account = "Account";
    public const string RateLimitCheck = "RateLimitCheck";
    public const string Repository = "Repository";
    public const string Other = "Other";

    public static string Categorize(string? requestUri)
    {
        if (string.IsNullOrWhiteSpace(requestUri))
            return Other;

        // Query string never affects the category, only the path.
        var path = requestUri.Split('?', 2)[0].TrimStart('/');

        if (path.Contains("/actions/", StringComparison.OrdinalIgnoreCase))
            return Actions;

        if (path.Contains("/pulls", StringComparison.OrdinalIgnoreCase))
            return PullRequests;

        if (path.Contains("/collaborators", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/teams", StringComparison.OrdinalIgnoreCase))
            return Reviewers;

        if (path.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
            return RateLimitCheck;

        if (path.Equals("user", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("user/", StringComparison.OrdinalIgnoreCase))
            return Account;

        if (path.StartsWith("repos/", StringComparison.OrdinalIgnoreCase))
            return Repository;

        return Other;
    }
}
