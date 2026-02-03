using System.Diagnostics;

namespace GrayMoon.Agent.Cli;

internal static class UninstallCommandHandler
{
    public static async Task<int> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return await UninstallWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
            return await UninstallSystemdAsync(cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine("Uninstall is supported only on Windows and Linux.");
        return 1;
    }

    private static async Task<int> UninstallWindowsAsync(CancellationToken cancellationToken)
    {
        var (exitCode, _, stderr) = await RunProcessAsync("sc", $"delete {InstallCommandHandler.ServiceName}", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"Failed to delete Windows service: {stderr?.TrimEnd() ?? "unknown"}");
            return exitCode;
        }
        Console.WriteLine($"Windows service '{InstallCommandHandler.ServiceName}' removed.");
        return 0;
    }

    private static async Task<int> UninstallSystemdAsync(CancellationToken cancellationToken)
    {
        var (exitCode, _, stderr) = await RunProcessAsync("systemctl", $"disable {InstallCommandHandler.ServiceName}.service --now", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"Failed to disable service: {stderr?.TrimEnd()}");
            return exitCode;
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

        await RunProcessAsync("systemctl", "daemon-reload", cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"systemd unit '{InstallCommandHandler.ServiceName}' removed.");
        return 0;
    }

    private static async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        process.Start();
        var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await outTask.ConfigureAwait(false);
        var stderr = await errTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
