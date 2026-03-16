namespace GrayMoon.App.Models;

/// <summary>Display model for a repository's pull request state (from GitHub API).</summary>
public sealed class PullRequestInfo
{
    public int Number { get; set; }
    public string State { get; set; } = string.Empty; // "open", "closed"
    public bool IsMerged => MergedAt.HasValue;
    public bool IsClosed => string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
    public DateTimeOffset? MergedAt { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    /// <summary>True = mergeable, false = conflict, null = unknown.</summary>
    public bool? Mergeable { get; set; }
    /// <summary>e.g. unknown, clean, dirty, unstable, blocked.</summary>
    public string? MergeableState { get; set; }
}
