using System.Runtime.Versioning;
using System.ServiceProcess;

namespace GrayMoon.Agent.Cli;

internal static class StopCommandHandler
{
    public static Task<int> StopAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Stop is only supported on Windows.");
            return Task.FromResult(1);
        }

        return Task.FromResult(StopWindows());
    }

    [SupportedOSPlatform("windows")]
    private static int StopWindows()
    {
        using var controller = new ServiceController(InstallCommandHandler.ServiceName);
        try
        {
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                Console.WriteLine("Service is already stopped.");
                return 0;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            Console.WriteLine("Service stopped.");
            return 0;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"Service '{InstallCommandHandler.ServiceName}' not found.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to stop service: {ex.Message}");
            return 1;
        }
    }
}
