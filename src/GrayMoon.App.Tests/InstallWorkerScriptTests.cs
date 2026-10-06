using System.Reflection;
using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Tests;

public sealed class InstallWorkerScriptTests
{
    [Fact]
    public void InstallScript_StaysOpenForManualRun_AndExitsForDesktopWindow()
    {
        var assembly = typeof(WorkerInstallService).Assembly;
        using var stream = assembly.GetManifestResourceStream("GrayMoon.App.Resources.install-worker.ps1");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.StartsWith("# GrayMoon Worker Installation Script", script.TrimStart());
        Assert.Contains("GRAYMOON_DESKTOP_INSTALL", script, StringComparison.Ordinal);
        Assert.Contains("Complete-WorkerInstall -Code 1", script, StringComparison.Ordinal);
        Assert.Contains("Complete-WorkerInstall -Code 0", script, StringComparison.Ordinal);
        Assert.Contains("Press Enter to close this window.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.Ordinal);

        var guard = script.IndexOf("if ($env:GRAYMOON_DESKTOP_INSTALL -ne '1')", StringComparison.Ordinal);
        var exit = script.IndexOf("exit $Code", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.True(exit > guard);
    }

    [Fact]
    public void InstallScript_ReportsStaleLogonPasswordFromAnUnattendedUpgrade()
    {
        var assembly = typeof(WorkerInstallService).Assembly;
        using var stream = assembly.GetManifestResourceStream("GrayMoon.App.Resources.install-worker.ps1");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.Contains("GRAYMOON_WORKER_NONINTERACTIVE", script, StringComparison.Ordinal);
        Assert.Contains("Enable-UnattendedWorkerInstall", script, StringComparison.Ordinal);
        Assert.Contains("-NonInteractive", script, StringComparison.Ordinal);
        Assert.Contains("install --hub-url $hubUrl --non-interactive", script, StringComparison.Ordinal);
        Assert.Contains("if ($installExit -eq 2)", script, StringComparison.Ordinal);
        Assert.Contains("/api/worker/install-failure", script, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"logon-password\"", script, StringComparison.Ordinal);
        Assert.Contains("Report-WorkerInstallFailure", script, StringComparison.Ordinal);
    }
}
