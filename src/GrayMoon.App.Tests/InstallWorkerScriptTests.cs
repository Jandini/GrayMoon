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
    [Fact]
    public void InstallScript_SerializesInstallsAndVerifiesTheExtract()
    {
        var assembly = typeof(WorkerInstallService).Assembly;
        using var stream = assembly.GetManifestResourceStream("GrayMoon.App.Resources.install-worker.ps1");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        // Two overlapping installs delete each other's extracted files (a Worker without git2-*.dll), so the lock
        // must be taken before the service is stopped and the folder wiped, and released on every path.
        var lockTaken = script.IndexOf(@"Global\GrayMoonWorkerInstall", StringComparison.Ordinal);
        var serviceStopped = script.IndexOf("Stop-Service", StringComparison.Ordinal);
        var folderWiped = script.IndexOf("Remove-Item -Recurse -Force", StringComparison.Ordinal);
        Assert.True(lockTaken >= 0);
        Assert.True(serviceStopped > lockTaken);
        Assert.True(folderWiped > lockTaken);
        Assert.Contains("AbandonedMutexException", script, StringComparison.Ordinal);
        Assert.Contains("ReleaseMutex()", script, StringComparison.Ordinal);

        // A shared archive path lets one run overwrite or delete the other's zip.
        Assert.DoesNotContain("'graymoon-worker-windows-install.zip'", script, StringComparison.Ordinal);
        Assert.Contains("NewGuid()", script, StringComparison.Ordinal);

        // Every archive entry must be on disk before the service is installed.
        var verified = script.IndexOf("The worker extract is incomplete", StringComparison.Ordinal);
        var installed = script.IndexOf("& $workerExe install", StringComparison.Ordinal);
        Assert.True(verified >= 0);
        Assert.True(installed > verified);
    }
}
