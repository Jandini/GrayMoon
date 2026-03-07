namespace GrayMoon.App.Models;

/// <summary>Display model for a repository's pull request state (from GitHub API).</summary>
public sealed class PullRequestInfo
{
    public int Number { get; set; }
    public string State { get; set; } = string.Empty; // "open", "closed"
    public bool IsMerged => MergedAt.HasValue;
    public DateTimeOffset? MergedAt { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
}
