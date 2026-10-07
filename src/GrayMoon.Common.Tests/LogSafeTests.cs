namespace GrayMoon.Common.Tests;

public sealed class LogSafeTests
{
    [Fact]
    public void ForLog_RedactsHttpExtraHeaderValue()
    {
        var input = "-c core.askpass=true -c \"http.extraHeader=Authorization: Basic c2VjcmV0\" fetch origin";

        var result = LogSafe.ForLog(input);

        Assert.DoesNotContain("c2VjcmV0", result, StringComparison.Ordinal);
        Assert.Contains("fetch origin", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("> Authorization: Basic c2VjcmV0")]
    [InlineData("authorization: bearer ghp_abc123.DEF-456")]
    [InlineData("GIT_CONFIG_VALUE_2=Authorization: Basic c2VjcmV0==")]
    public void ForLog_RedactsAuthorizationHeadersOutsideTheExtraHeaderForm(string input)
    {
        var result = LogSafe.ForLog(input);

        Assert.DoesNotContain("c2VjcmV0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abc123", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLog_LeavesOrdinaryTextAlone()
        => Assert.Equal("fetch origin --prune", LogSafe.ForLog("fetch origin --prune"));
}
