using System.Runtime.Versioning;
using System.ServiceProcess;
using GrayMoon.Agent.Platform.Windows;
using GrayMoon.Common;

namespace GrayMoon.Agent.Cli;

internal static class UninstallCommandHandler
{
    public static async Task<int> UninstallAsync(CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        if (OperatingSystem.IsWindows())
            return UninstallWindows();
        if (OperatingSystem.IsLinux())
            return await UninstallSystemdAsync(cancellationToken, commandLine).ConfigureAwait(false);

        Console.Error.WriteLine("Uninstall is supported only on Windows and Linux.");
        return 1;
    }

    [SupportedOSPlatform("windows")]
    private static int UninstallWindows()
    {
        using var controller = new ServiceController(InstallCommandHandler.ServiceName);
        try
        {
            _ = controller.Status;
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Service not found.");
            return 0;
        }

        if (controller.Status == ServiceControllerStatus.Running)
        {
            Console.WriteLine("Stopping service...");
            try
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to stop service: {ex.Message}");
                return 1;
            }
        }

        try
        {
            WindowsServiceManager.RemoveService(InstallCommandHandler.ServiceName);
            Console.WriteLine($"Service '{InstallCommandHandler.ServiceName}' removed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to remove service: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> UninstallSystemdAsync(CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        var result = await commandLine.RunAsync("systemctl", $"disable {InstallCommandHandler.ServiceName}.service --now", null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to disable service: {result.Stderr?.TrimEnd()}");
            return result.ExitCode;
        }

        var unitPath = $"/etc/systemd/system/{InstallCommandHandler.ServiceName}.service";
        if (File.Exists(unitPath))
        {
            try
            {
                File.Delete(unitPath);
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot remove {unitPath}. Run with sudo.");
                return 1;
            }
        }

        await commandLine.RunAsync("systemctl", "daemon-reload", null, null, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"systemd unit '{InstallCommandHandler.ServiceName}' removed.");
        return 0;
    }
}
