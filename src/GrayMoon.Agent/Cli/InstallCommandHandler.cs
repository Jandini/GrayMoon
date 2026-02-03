using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GrayMoon.Agent.Cli;

internal static class InstallCommandHandler
{
    public const string ServiceName = "GrayMoonAgent";

    public static async Task<int> InstallAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Console.Error.WriteLine("Could not determine agent executable path.");
            return 1;
        }

        var runArgs = AgentCliOptions.BuildRunArguments(parseResult);
        if (OperatingSystem.IsWindows())
            return await InstallWindowsAsync(exePath, runArgs, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
            return await InstallSystemdAsync(exePath, runArgs, cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine("Install is supported only on Windows and Linux.");
        return 1;
    }

    private static async Task<int> InstallWindowsAsync(string exePath, string runArgs, CancellationToken cancellationToken)
    {
        var binPath = $"{exePath} {runArgs}".TrimEnd();
        var (exitCode, stdout, stderr) = await RunProcessAsync("sc", $"create {ServiceName} binPath= \"{binPath}\" start= auto", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"Failed to create Windows service: {stderr?.TrimEnd() ?? stdout?.TrimEnd() ?? "unknown"}");
            return exitCode;
        }
        Console.WriteLine($"Windows service '{ServiceName}' installed. Start with: sc start {ServiceName}");
        return 0;
    }

    private static async Task<int> InstallSystemdAsync(string exePath, string runArgs, CancellationToken cancellationToken)
    {
        var unitPath = $"/etc/systemd/system/{ServiceName}.service";
        var unitContent = new StringBuilder();
        unitContent.AppendLine("[Unit]");
        unitContent.AppendLine("Description=GrayMoon Agent");
        unitContent.AppendLine("After=network.target");
        unitContent.AppendLine();
        unitContent.AppendLine("[Service]");
        unitContent.AppendLine($"ExecStart={exePath} {runArgs}");
        unitContent.AppendLine("Restart=on-failure");
        unitContent.AppendLine("RestartSec=5");
        unitContent.AppendLine();
        unitContent.AppendLine("[Install]");
        unitContent.AppendLine("WantedBy=multi-user.target");

        try
        {
            await File.WriteAllTextAsync(unitPath, unitContent.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot write {unitPath}. Run with sudo to install the systemd unit.");
            return 1;
        }

        var (exitCode, _, stderr) = await RunProcessAsync("systemctl", "daemon-reload", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"daemon-reload failed: {stderr?.TrimEnd()}");
            return exitCode;
        }

        (exitCode, _, stderr) = await RunProcessAsync("systemctl", $"enable {ServiceName}.service", cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"Failed to enable service: {stderr?.TrimEnd()}");
            return exitCode;
        }

        Console.WriteLine($"systemd unit installed: {unitPath}. Start with: sudo systemctl start {ServiceName}");
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
