using System.Net;
using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class GitHubApiErrorHelperTests
{
    private const string HeadInvalidBody =
        """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid"}],"documentation_url":"https://docs.github.com/rest/pulls/pulls#create-a-pull-request","status":"422"}""";

    private const string NoCommitsBetweenBody =
        """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"No commits between main and feature"}],"documentation_url":"https://docs.github.com/rest/pulls/pulls#create-a-pull-request","status":"422"}""";

    private const string RateLimitExceededBody =
        """{"message":"API rate limit exceeded for user.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api"}""";

    [Fact]
    public void CreateHttpRequestException_UnprocessableEntityWithRateLimitHeaders_DoesNotAppendResetSuffix()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "4950");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(HttpStatusCode.UnprocessableEntity, HeadInvalidBody, response);

        Assert.Equal("Validation Failed", ex.Message);
        Assert.DoesNotContain("GitHub will allow API requests again", ex.Message);
    }

    [Fact]
    public void FormatFriendlyGitHubHttpError_HeadBranchInvalid_ReportsUnpushedBranchNotRateLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "4950");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(HttpStatusCode.UnprocessableEntity, HeadInvalidBody, response);
        var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);

        Assert.DoesNotContain("rate limit", friendly, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GitHub will allow API requests again", friendly);
        Assert.Contains("Push your local commits first", friendly);
    }

    [Fact]
    public void FormatFriendlyGitHubHttpError_NoCommitsBetween_ReportsUnpushedBranchNotRateLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        response.Headers.Add("X-RateLimit-Remaining", "4998");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(HttpStatusCode.UnprocessableEntity, NoCommitsBetweenBody, response);
        var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);

        Assert.DoesNotContain("GitHub will allow API requests again", friendly);
        Assert.Contains("Push your local commits first", friendly);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void FormatFriendlyGitHubHttpError_NonRateLimitStatusesWithRateLimitHeaders_DoNotAppendResetSuffix(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "4900");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(status, """{"message":"Some unrelated error"}""", response);
        var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);

        Assert.DoesNotContain("GitHub will allow API requests again", friendly);
        Assert.DoesNotContain("rate limit", friendly, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatFriendlyGitHubHttpError_TooManyRequests_StillAppendsResetSuffix()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(HttpStatusCode.TooManyRequests, RateLimitExceededBody, response);
        var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);

        Assert.Contains("GitHub will allow API requests again", friendly);
        Assert.Contains("rate limit", friendly, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatFriendlyGitHubHttpError_ForbiddenWithoutRateLimitWording_StillAppendsResetSuffix()
    {
        // GitHub uses 403 ambiguously for both permission issues and secondary rate limits, so the reset
        // suffix is kept even when the message text itself doesn't mention rate limiting.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1700000000");

        var ex = GitHubApiErrorHelper.CreateHttpRequestException(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""", response);
        var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);

        Assert.Contains("GitHub will allow API requests again", friendly);
    }

    [Fact]
    public void LooksLikeUnpushedHeadBranch_DetectsHeadInvalidField()
    {
        Assert.True(GitHubApiErrorHelper.LooksLikeUnpushedHeadBranch(HeadInvalidBody));
    }

    [Fact]
    public void LooksLikeUnpushedHeadBranch_DetectsNoCommitsBetweenMessage()
    {
        Assert.True(GitHubApiErrorHelper.LooksLikeUnpushedHeadBranch(NoCommitsBetweenBody));
    }

    [Fact]
    public void LooksLikeUnpushedHeadBranch_ReturnsFalseForUnrelatedError()
    {
        Assert.False(GitHubApiErrorHelper.LooksLikeUnpushedHeadBranch("""{"message":"Not Found"}"""));
    }
}
