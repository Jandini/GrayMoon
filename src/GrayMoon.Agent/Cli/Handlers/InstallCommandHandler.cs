using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using GrayMoon.Agent.Platform.Windows;
using GrayMoon.Common;

namespace GrayMoon.Agent.Cli;

internal static class InstallCommandHandler
{
    public const string ServiceName = "GrayMoonAgent";
    private const string ServiceDisplayName = "GrayMoon Agent";
    private const string ServiceDescription = "Host-side agent for GrayMoon: executes git and repository I/O operations";

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
            return InstallWindows(exePath, runArgs, parseResult);
        if (OperatingSystem.IsLinux())
            return await InstallSystemdAsync(exePath, runArgs, cancellationToken, commandLine).ConfigureAwait(false);

        Console.Error.WriteLine("Install is supported only on Windows and Linux.");
        return 1;
    }

    [SupportedOSPlatform("windows")]
    private static int InstallWindows(string exePath, string runArgs, ParseResult parseResult)
    {
        var binPath = $"\"{exePath}\" {runArgs}".TrimEnd();

        ServiceController? existing = null;
        bool serviceExists;
        try
        {
            existing = new ServiceController(ServiceName);
            _ = existing.Status;
            serviceExists = true;
        }
        catch (InvalidOperationException)
        {
            existing?.Dispose();
            existing = null;
            serviceExists = false;
        }

        try
        {
            return serviceExists && existing != null
                ? UpdateWindows(existing, binPath)
                : FreshInstallWindows(binPath, parseResult);
        }
        finally
        {
            existing?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static int UpdateWindows(ServiceController controller, string binPath)
    {
        if (controller.Status == ServiceControllerStatus.Running)
        {
            Console.WriteLine("Stopping running service...");
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
            WindowsServiceManager.UpdateServiceBinPath(ServiceName, binPath);
            WindowsServiceManager.SetServiceDescription(ServiceName, ServiceDescription);
            Console.WriteLine("Service configuration updated.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to update service: {ex.Message}");
            return 1;
        }

        return StartWindows();
    }

    [SupportedOSPlatform("windows")]
    private static int FreshInstallWindows(string binPath, ParseResult parseResult)
    {
        var account = parseResult.GetValue(AgentCliOptions.Account)
            ?? WindowsIdentity.GetCurrent().Name;

        Console.WriteLine($"Service will run as: {account}");

        string? password = null;

        if (!IsVirtualAccount(account))
        {
            Console.Write($"Password for {account}: ");
            password = ReadPasswordMasked();

            var (domain, username) = ParseAccountName(account);
            if (!WindowsCredentialValidator.Validate(username, domain, password))
            {
                password = new string('\0', password.Length);
                Console.Error.WriteLine("Invalid credentials.");
                return 1;
            }

            Console.WriteLine("Granting 'Log on as a service' right...");
            try
            {
                var ntAccount = new NTAccount(account);
                var sid = (SecurityIdentifier)ntAccount.Translate(typeof(SecurityIdentifier));
                WindowsLsaPolicy.GrantServiceLogonRight(sid);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to grant service logon right: {ex.Message}");
                return 1;
            }
        }

        try
        {
            Console.WriteLine("Creating Windows service...");
            WindowsServiceManager.CreateService(ServiceName, ServiceDisplayName, binPath, account, password);
            WindowsServiceManager.SetServiceDescription(ServiceName, ServiceDescription);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to create service: {ex.Message}");
            return 1;
        }
        finally
        {
            if (password != null)
            {
                password = new string('\0', password.Length);
                GC.Collect();
            }
        }

        return StartWindows();
    }

    [SupportedOSPlatform("windows")]
    private static int StartWindows()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service '{ServiceName}' installed and started successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start service: {ex.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsVirtualAccount(string account) =>
        account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
        account.Equals(@"NT AUTHORITY\LocalSystem", StringComparison.OrdinalIgnoreCase) ||
        account.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) ||
        account.Equals(@"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase) ||
        account.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ||
        account.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase);

    private static (string Domain, string Username) ParseAccountName(string accountName)
    {
        if (accountName.Contains('\\'))
        {
            var parts = accountName.Split('\\', 2);
            return (parts[0], parts[1]);
        }
        if (accountName.Contains('@'))
        {
            var parts = accountName.Split('@', 2);
            return (parts[1], parts[0]);
        }
        return (Environment.MachineName, accountName);
    }

    private static string ReadPasswordMasked()
    {
        var sb = new StringBuilder();
        ConsoleKeyInfo key;
        do
        {
            key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (key.Key != ConsoleKey.Enter && key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        while (key.Key != ConsoleKey.Enter);
        Console.WriteLine();
        return sb.ToString();
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
