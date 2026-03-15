using System.CommandLine;
using System.Text;
using GrayMoon.Common;

namespace GrayMoon.Agent.Cli;

internal static class InstallCommandHandler
{
    public const string ServiceName = "GrayMoonAgent";

    public static async Task<int> InstallAsync(ParseResult parseResult, CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Console.Error.WriteLine("Could not determine agent executable path.");
            return 1;
        }

        var runArgs = AgentCliOptions.BuildRunArguments(parseResult);
        if (OperatingSystem.IsWindows())
            return await InstallWindowsAsync(exePath, runArgs, cancellationToken, commandLine).ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
            return await InstallSystemdAsync(exePath, runArgs, cancellationToken, commandLine).ConfigureAwait(false);

        Console.Error.WriteLine("Install is supported only on Windows and Linux.");
        return 1;
    }

    private static async Task<int> InstallWindowsAsync(string exePath, string runArgs, CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        var binPath = $"{exePath} {runArgs}".TrimEnd();
        var result = await commandLine.RunAsync("sc", $"create {ServiceName} binPath= \"{binPath}\" start= auto", null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to create Windows service: {result.Stderr?.TrimEnd() ?? result.Stdout?.TrimEnd() ?? "unknown"}");
            return result.ExitCode;
        }
        Console.WriteLine($"Windows service '{ServiceName}' installed. Start with: sc start {ServiceName}");
        return 0;
    }

    private static async Task<int> InstallSystemdAsync(string exePath, string runArgs, CancellationToken cancellationToken, ICommandLineService commandLine)
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

        var result = await commandLine.RunAsync("systemctl", "daemon-reload", null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"daemon-reload failed: {result.Stderr?.TrimEnd()}");
            return result.ExitCode;
        }

        result = await commandLine.RunAsync("systemctl", $"enable {ServiceName}.service", null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Failed to enable service: {result.Stderr?.TrimEnd()}");
            return result.ExitCode;
        }

        Console.WriteLine($"systemd unit installed: {unitPath}. Start with: sudo systemctl start {ServiceName}");
        return 0;
    }
}
