using GrayMoon.Agent.Platform.Windows;

namespace GrayMoon.Agent.Tests;

public sealed class HostEnvironmentPathTests
{
    [Fact]
    public void MergePath_combines_machine_user_and_process_in_order()
    {
        var merged = HostEnvironmentPath.MergePath(
            machinePath: @"C:\Machine\A;C:\Machine\B",
            userPath: @"C:\User\A",
            processPath: @"C:\Process\A",
            dotnetToolsDirectory: null);

        Assert.Equal(@"C:\Machine\A;C:\Machine\B;C:\User\A;C:\Process\A", merged);
    }

    [Fact]
    public void MergePath_deduplicates_case_insensitively_and_keeps_first_occurrence()
    {
        var merged = HostEnvironmentPath.MergePath(
            machinePath: @"C:\Tools;C:\Git\cmd",
            userPath: @"c:\tools;C:\UserTools",
            processPath: @"C:\GIT\cmd;C:\Extra",
            dotnetToolsDirectory: null);

        Assert.Equal(@"C:\Tools;C:\Git\cmd;C:\UserTools;C:\Extra", merged);
    }

    [Fact]
    public void MergePath_includes_dotnet_tools_once_when_supplied()
    {
        var tools = @"C:\Users\dev\.dotnet\tools";
        var merged = HostEnvironmentPath.MergePath(
            machinePath: @"C:\Windows\System32",
            userPath: tools,
            processPath: null,
            dotnetToolsDirectory: tools);

        Assert.Equal(@"C:\Windows\System32;" + tools, merged);
    }

    [Fact]
    public void MergePath_appends_dotnet_tools_when_not_already_present()
    {
        var tools = @"C:\Users\dev\.dotnet\tools";
        var merged = HostEnvironmentPath.MergePath(
            machinePath: @"C:\Windows\System32",
            userPath: @"C:\Users\dev\bin",
            processPath: null,
            dotnetToolsDirectory: tools);

        Assert.Equal(@"C:\Windows\System32;C:\Users\dev\bin;" + tools, merged);
    }

    [Theory]
    [InlineData(null, null, null, null, "")]
    [InlineData("", "   ", null, null, "")]
    [InlineData("C:\\A;;C:\\B;", null, null, null, @"C:\A;C:\B")]
    public void MergePath_handles_null_and_empty_values(
        string? machine,
        string? user,
        string? process,
        string? tools,
        string expected)
    {
        var merged = HostEnvironmentPath.MergePath(machine, user, process, tools);
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void MergePath_preserves_process_only_entries_after_machine_and_user()
    {
        var merged = HostEnvironmentPath.MergePath(
            machinePath: @"C:\Windows",
            userPath: @"C:\Users\dev\bin",
            processPath: @"C:\Windows;C:\Users\dev\bin;C:\SCM\Extra",
            dotnetToolsDirectory: null);

        Assert.Equal(@"C:\Windows;C:\Users\dev\bin;C:\SCM\Extra", merged);
    }
}
