using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Entry point of the test assembly when a test starts it as a child process (<c>dotnet GrayMoon.Worker.Tests.dll
/// &lt;argument&gt;</c>). The test runner never calls it. Used for checks that need a process of their own, such as
/// loading a native library that this process has already loaded.
/// </summary>
internal static class TestProcessEntryPoint
{
    public const string InstallProbeArgument = "--install-probe";
    public const string ProbeResultPrefix = "PROBE:";
    public const string Intact = "intact";

    private static int Main(string[] args)
    {
        if (args is [InstallProbeArgument])
        {
            Console.WriteLine(ProbeResultPrefix + (new WorkerInstallProbe().Check() ?? Intact));
            return 0;
        }

        Console.Error.WriteLine("This assembly is run by the test runner.");
        return 1;
    }
}
