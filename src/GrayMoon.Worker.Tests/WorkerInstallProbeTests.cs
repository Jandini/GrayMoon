using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorkerInstallProbeTests
{
    [Fact]
    public void An_intact_install_reports_no_damage()
    {
        Assert.Null(new WorkerInstallProbe().Check());
    }
}
