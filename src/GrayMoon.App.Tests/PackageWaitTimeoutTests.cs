using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class PackageWaitTimeoutTests
{
    [Fact]
    public void FormatMessage_is_timeout_not_user_cancel()
    {
        var message = PackageWaitTimeout.FormatMessage(found: 1, total: 3, timeout: TimeSpan.FromMinutes(6));

        Assert.Contains("Timed out waiting for package dependencies", message, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 of 3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_writes_timeout_on_the_level_once()
    {
        var errors = new Dictionary<int, string>();

        PackageWaitTimeout.Report(2, (level, msg) => errors[level] = msg, found: 0, total: 2, timeout: TimeSpan.FromMinutes(3));

        Assert.Single(errors);
        Assert.True(errors.ContainsKey(2));
        Assert.Contains("Timed out waiting for package dependencies", errors[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Push cancelled", errors[2], StringComparison.OrdinalIgnoreCase);
    }
}
