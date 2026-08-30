using System.Net;

namespace GrayMoon.App.Services.GitHub;

/// <summary>GitHub REST failure with optional rate-limit headers from the response.</summary>
public sealed class GitHubHttpRequestException : HttpRequestException
{
    public GitHubRateLimitSnapshot? RateLimit { get; }

    /// <summary>Raw JSON error body from GitHub, if any, for callers that need to inspect the <c>errors</c> array.</summary>
    public string? RawErrorContent { get; }

    public GitHubHttpRequestException(
        string message,
        HttpStatusCode statusCode,
        GitHubRateLimitSnapshot? rateLimit,
        string? rawErrorContent = null)
        : base(message, inner: null, statusCode: statusCode)
    {
        RateLimit = rateLimit;
        RawErrorContent = rawErrorContent;
    }
}

/// <summary>Values from GitHub <c>x-ratelimit-*</c> response headers (reset is UTC epoch seconds).</summary>
public readonly record struct GitHubRateLimitSnapshot(
    int? Limit,
    int? Remaining,
    int? Used,
    long? ResetEpochUtcSeconds)
{
    public DateTimeOffset? ResetUtc => ResetEpochUtcSeconds.HasValue
        ? DateTimeOffset.FromUnixTimeSeconds(ResetEpochUtcSeconds.Value)
        : null;
}
