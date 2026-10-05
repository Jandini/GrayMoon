using GrayMoon.App.Services.Features;
using Xunit;

namespace GrayMoon.App.Tests;

public class FeatureOperationLogTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("fatal: not a git repository", "fatal: not a git repository")]
    [InlineData(
        "fatal: unable to access 'https://x-access-token:ghp_secret123@github.com/org/repo.git/': 403",
        "fatal: unable to access 'https://***@github.com/org/repo.git/': 403")]
    [InlineData(
        "failed: https://user@host/a and https://tok:en@host/b",
        "failed: https://***@host/a and https://***@host/b")]
    public void Redact_removes_credentials_from_urls_and_leaves_other_text_alone(string? input, string? expected)
    {
        Assert.Equal(expected, FeatureOperationLog.Redact(input));
    }
}
