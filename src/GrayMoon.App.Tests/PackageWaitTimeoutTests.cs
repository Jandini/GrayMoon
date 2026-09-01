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
    public void Report_writes_timeout_on_each_waiting_repo()
    {
        var errors = new Dictionary<int, string>();

        PackageWaitTimeout.Report([11, 22], (id, msg) => errors[id] = msg, found: 0, total: 2, timeout: TimeSpan.FromMinutes(3));

        Assert.Equal(2, errors.Count);
        Assert.Equal(errors[11], errors[22]);
        Assert.Contains("Timed out waiting for package dependencies", errors[11], StringComparison.Ordinal);
        Assert.DoesNotContain("Push cancelled", errors[11], StringComparison.OrdinalIgnoreCase);
    }
}
