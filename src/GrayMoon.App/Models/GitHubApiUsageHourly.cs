namespace GrayMoon.App.Models;

/// <summary>
/// One row per (connector, category, UTC hour bucket), accumulated from in-memory counters and flushed
/// periodically by <c>GitHubApiUsageRecorder</c>. Persisted (rather than kept purely in memory) because
/// GrayMoon is restarted frequently during development, and the point of this table is to see usage trends
/// across restarts within the current GitHub rate-limit window.
/// </summary>
public class GitHubApiUsageHourly
{
    public int GitHubApiUsageHourlyId { get; set; }

    public int ConnectorId { get; set; }

    /// <summary>Coarse call category, e.g. "Actions", "PullRequests", "Reviewers", "Repository", "Account", "Other".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>UTC timestamp truncated to the top of the hour.</summary>
    public DateTime HourBucketUtc { get; set; }

    /// <summary>Total HTTP calls made (every attempt, including ones Polly retried).</summary>
    public int RequestCount { get; set; }

    /// <summary>How many of <see cref="RequestCount"/> were conditional-GET 304 responses (free against the primary limit).</summary>
    public int NotModifiedCount { get; set; }

    /// <summary>How many of <see cref="RequestCount"/> ended in a non-success status code.</summary>
    public int ErrorCount { get; set; }
}
