using System.Runtime.Versioning;
using System.ServiceProcess;

namespace GrayMoon.Agent.Cli;

internal static class StartCommandHandler
{
    public static Task<int> StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Start is only supported on Windows.");
            return Task.FromResult(1);
        }

        return Task.FromResult(StartWindows());
    }

    [SupportedOSPlatform("windows")]
    private static int StartWindows()
    {
        using var controller = new ServiceController(InstallCommandHandler.ServiceName);
        try
        {
            if (controller.Status == ServiceControllerStatus.Running)
            {
                Console.WriteLine("Service is already running.");
                return 0;
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine("Service started.");
            return 0;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"Service '{InstallCommandHandler.ServiceName}' not found.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start service: {ex.Message}");
            return 1;
        }
    }
}
