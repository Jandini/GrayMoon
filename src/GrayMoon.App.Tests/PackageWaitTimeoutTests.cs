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

    [Fact]
    public void FormatMessage_names_missing_packages_without_naming_a_ci_product()
    {
        var message = PackageWaitTimeout.FormatMessage(1, 2, TimeSpan.FromMinutes(5), ["Edx1.Pkg 1.0.0"]);

        Assert.Contains("Edx1.Pkg 1.0.0", message, StringComparison.Ordinal);
        Assert.Contains("Run Push Synchronized again", message, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatMessage_names_a_read_only_producer_and_does_not_tell_the_user_to_wait_and_push_again()
    {
        var message = PackageWaitTimeout.FormatMessage(1, 2, TimeSpan.FromMinutes(5), ["Edx1.Pkg 1.0.0"], ["acme-api"]);

        Assert.Contains("Edx1.Pkg 1.0.0", message, StringComparison.Ordinal);
        Assert.Contains("Repository acme-api produces a missing package and is read-only", message, StringComparison.Ordinal);
        Assert.Contains("GrayMoon does not push it", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Push Synchronized again", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessage_names_every_read_only_producer()
    {
        var message = PackageWaitTimeout.FormatMessage(0, 2, TimeSpan.FromMinutes(5), ["A 1.0.0", "B 1.0.0"], ["acme-api", "acme-core"]);

        Assert.Contains("Repositories acme-api, acme-core produce missing packages and are read-only", message, StringComparison.Ordinal);
        Assert.Contains("GrayMoon does not push them", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_passes_the_read_only_producers_on()
    {
        var errors = new Dictionary<int, string>();

        PackageWaitTimeout.Report(1, (level, msg) => errors[level] = msg, 0, 1, TimeSpan.FromMinutes(3), ["A 1.0.0"], ["acme-api"]);

        Assert.Contains("acme-api", errors[1], StringComparison.Ordinal);
    }
}
