using System.Net;

namespace GrayMoon.App.Services;

/// <summary>GitHub REST failure with optional rate-limit headers from the response.</summary>
public sealed class GitHubHttpRequestException : HttpRequestException
{
    public GitHubRateLimitSnapshot? RateLimit { get; }

    public GitHubHttpRequestException(
        string message,
        HttpStatusCode statusCode,
        GitHubRateLimitSnapshot? rateLimit)
        : base(message, inner: null, statusCode: statusCode)
    {
        RateLimit = rateLimit;
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
