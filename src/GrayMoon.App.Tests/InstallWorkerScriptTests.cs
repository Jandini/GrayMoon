using System.Reflection;
using GrayMoon.App.Services.Agent;

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
}
