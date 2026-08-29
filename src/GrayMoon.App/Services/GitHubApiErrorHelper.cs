using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrayMoon.App.Services;

/// <summary>Maps GitHub REST API failures to accurate user-facing messages (rate limits vs token/scopes).</summary>
public static class GitHubApiErrorHelper
{
    private const string ResetEpochMarker = "X-RateLimit-Reset:";

    private static readonly Regex RateLimitIndicatorsRegex = new(
        @"rate\s*limit|secondary\s+rate|abuse|too many requests",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericHttpClientMessageRegex = new(
        @"^Response status code does not indicate success",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Extracts the user-visible <c>message</c> field from a GitHub API JSON error body.</summary>
    public static string? TryParseGitHubApiUserMessage(string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch (JsonException)
        {
            /* not JSON */
        }

        return null;
    }

    /// <summary>
    /// Detects GitHub's "create pull request" validation errors caused by a head branch that does not exist on
    /// the remote yet (typically because local commits have not been pushed).
    /// </summary>
    public static bool LooksLikeUnpushedHeadBranch(string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var error in errors.EnumerateArray())
            {
                var field = error.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

                if (string.Equals(field, "head", StringComparison.OrdinalIgnoreCase) && string.Equals(code, "invalid", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(message) && message.Contains("No commits between", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            /* not JSON */
        }

        return false;
    }

    /// <summary>
    /// Parses <c>x-ratelimit-limit</c>, <c>x-ratelimit-remaining</c>, <c>x-ratelimit-used</c>, and
    /// <c>x-ratelimit-reset</c> (UTC epoch seconds) from a GitHub REST response.
    /// </summary>
    public static GitHubRateLimitSnapshot? TryParseRateLimitHeaders(HttpResponseMessage response)
    {
        return TryParseRateLimitHeaders(response.Headers)
               ?? (response.Content != null ? TryParseRateLimitHeaders(response.Content.Headers) : null);
    }

    public static bool LooksLikeRateLimit(HttpStatusCode? statusCode, string? githubMessage)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
            return true;
        return !string.IsNullOrWhiteSpace(githubMessage) && RateLimitIndicatorsRegex.IsMatch(githubMessage);
    }

    /// <summary>
    /// GitHub sends X-RateLimit-* headers on every response, success or failure, so their mere presence is not
    /// evidence of a rate-limit failure. Only append rate-limit-reset guidance when the status/message actually
    /// indicates rate limiting, or for 403 which GitHub also uses ambiguously for secondary/abuse limits.
    /// </summary>
    private static bool IsPlausibleRateLimitCondition(HttpStatusCode? statusCode, string? githubMessage)
        => LooksLikeRateLimit(statusCode, githubMessage) || statusCode == HttpStatusCode.Forbidden;

    public static bool IsRateLimitExhausted(HttpResponseMessage response)
    {
        var snapshot = TryParseRateLimitHeaders(response);
        if (snapshot?.Remaining is int remaining)
            return remaining <= 0;
        return false;
    }

    public static HttpRequestException CreateHttpRequestException(
        HttpStatusCode statusCode,
        string? errorContent,
        HttpResponseMessage? response = null)
    {
        var rateLimit = response != null ? TryParseRateLimitHeaders(response) : null;
        var detail = TryParseGitHubApiUserMessage(errorContent);
        var core = !string.IsNullOrWhiteSpace(detail)
            ? detail.Trim()
            : $"GitHub returned {(int)statusCode} ({statusCode}).";
        var message = IsPlausibleRateLimitCondition(statusCode, detail)
            ? AppendRateLimitReset(core, rateLimit)
            : core;
        return new GitHubHttpRequestException(message, statusCode, rateLimit, errorContent);
    }

    /// <summary>User-facing text for Actions error badges and connector tests.</summary>
    public static string FormatFriendlyGitHubHttpError(HttpRequestException ex)
    {
        var rateLimit = ex is GitHubHttpRequestException githubEx ? githubEx.RateLimit : null;
        var rawErrorContent = ex is GitHubHttpRequestException githubEx2 ? githubEx2.RawErrorContent : null;
        var status = ex.StatusCode;
        var detail = GetActionableDetail(ex.Message);
        var looksLikeRateLimit = LooksLikeRateLimit(status, detail);

        var core = looksLikeRateLimit
            ? BuildRateLimitUserMessage(status, detail)
            : status switch
            {
                HttpStatusCode.Unauthorized =>
                    string.IsNullOrWhiteSpace(detail)
                        ? "Unauthorized (401). Check the connector token on the Connectors page."
                        : $"Unauthorized (401). {detail}",
                HttpStatusCode.Forbidden => BuildForbiddenUserMessage(detail),
                HttpStatusCode.NotFound =>
                    string.IsNullOrWhiteSpace(detail)
                        ? "Not found (404). The repository or workflow was not found."
                        : $"Not found (404). {detail}",
                HttpStatusCode.Conflict =>
                    string.IsNullOrWhiteSpace(detail)
                        ? "Conflict (409). The workflow run may already be finished."
                        : detail,
                HttpStatusCode.UnprocessableEntity => BuildUnprocessableEntityUserMessage(detail, rawErrorContent),
                HttpStatusCode.TooManyRequests => BuildRateLimitUserMessage(status, detail),
                HttpStatusCode.ServiceUnavailable =>
                    string.IsNullOrWhiteSpace(detail)
                        ? "GitHub service unavailable (503). Try again later."
                        : $"GitHub service unavailable (503). {detail}",
                _ =>
                    string.IsNullOrWhiteSpace(detail)
                        ? "GitHub API request failed."
                        : detail
            };

        // X-RateLimit-* headers are present on every GitHub response, success or failure, so only append
        // reset guidance when the failure actually looks rate-limit related (or 403, which GitHub also uses
        // ambiguously for secondary/abuse limits) - not for unrelated errors like 422 validation failures.
        return IsPlausibleRateLimitCondition(status, detail)
            ? AppendRateLimitReset(core, rateLimit)
            : core;
    }

    /// <summary>
    /// Appends when <c>x-ratelimit-reset</c> is present so users know when the token may call the API again.
    /// </summary>
    public static string AppendRateLimitReset(string message, GitHubRateLimitSnapshot? snapshot)
    {
        if (snapshot?.ResetEpochUtcSeconds is not long epoch)
            return message;

        if (message.Contains(ResetEpochMarker, StringComparison.Ordinal))
            return message;

        var resetUtc = DateTimeOffset.FromUnixTimeSeconds(epoch);
        var usage = FormatUsageClause(snapshot.Value);

        return $"{message.TrimEnd()} GitHub will allow API requests again after {resetUtc:yyyy-MM-dd HH:mm:ss} UTC "
               + $"({ResetEpochMarker} {epoch}, Unix epoch UTC seconds){usage}";
    }

    private static string FormatUsageClause(GitHubRateLimitSnapshot snapshot)
    {
        if (snapshot.Used.HasValue && snapshot.Limit.HasValue)
        {
            return $" X-RateLimit-Used: {snapshot.Used.Value} of {snapshot.Limit.Value} in the current window.";
        }

        if (snapshot.Remaining.HasValue && snapshot.Limit.HasValue)
        {
            return $" X-RateLimit-Remaining: {snapshot.Remaining.Value} of {snapshot.Limit.Value}.";
        }

        return string.Empty;
    }

    private static GitHubRateLimitSnapshot? TryParseRateLimitHeaders(HttpHeaders? headers)
    {
        if (headers == null)
            return null;

        var limit = TryParseHeaderInt(headers, "X-RateLimit-Limit");
        var remaining = TryParseHeaderInt(headers, "X-RateLimit-Remaining");
        var used = TryParseHeaderInt(headers, "X-RateLimit-Used");
        var reset = TryParseHeaderLong(headers, "X-RateLimit-Reset");

        if (!limit.HasValue && !remaining.HasValue && !used.HasValue && !reset.HasValue)
            return null;

        return new GitHubRateLimitSnapshot(limit, remaining, used, reset);
    }

    private static int? TryParseHeaderInt(HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;
        return int.TryParse(values.FirstOrDefault(), out var n) ? n : null;
    }

    private static long? TryParseHeaderLong(HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;
        return long.TryParse(values.FirstOrDefault(), out var n) ? n : null;
    }

    private static string? GetActionableDetail(string? exceptionMessage)
    {
        if (string.IsNullOrWhiteSpace(exceptionMessage))
            return null;
        var trimmed = exceptionMessage.Trim();
        if (GenericHttpClientMessageRegex.IsMatch(trimmed))
            return null;

        var resetIdx = trimmed.IndexOf(" GitHub will allow API requests again after ", StringComparison.Ordinal);
        if (resetIdx > 0)
            trimmed = trimmed[..resetIdx];

        return trimmed;
    }

    private static string BuildUnprocessableEntityUserMessage(string? detail, string? rawErrorContent)
    {
        if (LooksLikeUnpushedHeadBranch(rawErrorContent))
        {
            return "GitHub could not find the head branch on the remote (422). "
                   + "Push your local commits first, then create the pull request.";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? "GitHub rejected the workflow request (422). It may not support manual runs on this branch, or required workflow inputs are missing."
            : detail;
    }

    private static string BuildForbiddenUserMessage(string? githubDetail)
    {
        if (!string.IsNullOrWhiteSpace(githubDetail))
        {
            return $"Forbidden (403). {githubDetail} "
                   + "If the same token works outside GrayMoon, this is often API rate limiting (GitHub returns 403, not only 429), not a bad token. "
                   + "Wait until the reset time below (if shown), use Refresh, and avoid many repos refreshing at once on Actions.";
        }

        return "Forbidden (403). GitHub denied the request. "
               + "This is not always a bad token: GitHub uses 403 for rate and abuse limits, and also for missing scopes (repo, workflow) or org SSO. "
               + "Check Connectors; if a reset time is shown below, wait until then and refresh.";
    }

    private static string BuildRateLimitUserMessage(HttpStatusCode? status, string? githubDetail)
    {
        var statusLabel = status.HasValue ? $" ({(int)status.Value})" : "";
        if (!string.IsNullOrWhiteSpace(githubDetail))
        {
            return $"GitHub rate limit{statusLabel}. {githubDetail} "
                   + "Wait until the reset time below, then use Refresh. Actions polls workflows and live job logs; large workspaces hit limits faster than a single manual API call.";
        }

        return $"GitHub rate limit{statusLabel}. Too many API requests. "
               + "Wait until the reset time below, then refresh. Actions polls workflows and live logs; many repositories refreshing together can exhaust the token limit quickly.";
    }
}
