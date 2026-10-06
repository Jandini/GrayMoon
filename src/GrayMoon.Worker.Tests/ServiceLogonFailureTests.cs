using System.ComponentModel;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Platform.Windows;

namespace GrayMoon.Worker.Tests;

public sealed class ServiceLogonFailureTests
{
    [Fact]
    public void Matches_error_1069_on_the_exception_or_its_inner_exception()
    {
        var direct = new Win32Exception(ServiceLogonFailure.ErrorServiceLogonFailed);
        Assert.True(ServiceLogonFailure.IsMatch(direct));

        var wrapped = new InvalidOperationException("Cannot start service 'GrayMoonWorker' on computer '.'.", direct);
        Assert.True(ServiceLogonFailure.IsMatch(wrapped));
        Assert.False(ServiceLogonFailure.IsMatch(new InvalidOperationException("Cannot start service 'GrayMoonWorker' on computer '.'.")));
        Assert.False(ServiceLogonFailure.IsMatch(new Win32Exception(5)));
    }
}

public sealed class SelfUpdateLaunchTests
{
    [Fact]
    public void Unattended_launch_marks_the_install_script_non_interactive()
    {
        var arguments = SelfUpdateCommand.BuildLaunchArguments("http://localhost:8384/api/worker/install");

        Assert.Contains("$env:" + SelfUpdateCommand.NonInteractiveVariable + "='1'", arguments, StringComparison.Ordinal);
        Assert.Contains("-NonInteractive", arguments, StringComparison.Ordinal);
        Assert.Contains("irm 'http://localhost:8384/api/worker/install' | iex", arguments, StringComparison.Ordinal);
    }
}
