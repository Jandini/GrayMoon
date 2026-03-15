using GrayMoon.Common;

namespace GrayMoon.Agent.Cli;

internal static class UninstallCommandHandler
{
    public static async Task<int> UninstallAsync(CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        if (OperatingSystem.IsWindows())
            return await UninstallWindowsAsync(cancellationToken, commandLine).ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
            return await UninstallSystemdAsync(cancellationToken, commandLine).ConfigureAwait(false);

        Console.Error.WriteLine("Uninstall is supported only on Windows and Linux.");
        return 1;
    }

    private static async Task<int> UninstallWindowsAsync(CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        var result = await commandLine.RunAsync("sc", $"delete {InstallCommandHandler.ServiceName}", null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to delete Windows service: {result.Stderr?.TrimEnd() ?? "unknown"}");
            return result.ExitCode;
        }
        Console.WriteLine($"Windows service '{InstallCommandHandler.ServiceName}' removed.");
        return 0;
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
