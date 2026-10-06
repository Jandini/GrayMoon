using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using GrayMoon.Worker.Platform.Windows;
using GrayMoon.Common;

namespace GrayMoon.Worker.Cli;

internal static class InstallCommandHandler
{
    public const string ServiceName = "GrayMoonWorker";
    /// <summary>Previous Windows/systemd service id; removed on install/uninstall so upgrades migrate cleanly.</summary>
    public const string LegacyServiceName = "GrayMoonAgent";
    /// <summary>
    /// Printed and returned when the service account password is no longer valid and nobody can type a new one.
    /// install-worker.ps1 treats this exit code as the unattended logon-password failure.
    /// </summary>
    internal const int LogonPasswordRequiredExitCode = 2;
    internal const string LogonPasswordRequiredMarker = "LOGON_PASSWORD_REQUIRED";
    private const int PasswordAttempts = 3;
    private const string ServiceDisplayName = "GrayMoon Worker";
    private const string ServiceDescription = "Host-side worker for GrayMoon: executes git and repository I/O operations";

    public static async Task<int> InstallAsync(ParseResult parseResult, CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Console.Error.WriteLine("Could not determine worker executable path.");
            return 1;
        }

        var runArgs = WorkerCliOptions.BuildRunArguments(parseResult);

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

        if (!RemoveWindowsServiceIfPresent(LegacyServiceName, announce: true))
            return 1;

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

        var interactive = !parseResult.GetValue(WorkerCliOptions.NonInteractive);
        try
        {
            return serviceExists && existing != null
                ? UpdateWindows(existing, binPath, interactive)
                : FreshInstallWindows(binPath, parseResult, interactive);
        }
        finally
        {
            existing?.Dispose();
        }
    }

    /// <summary>Stops and deletes a Windows service by name when it exists. Returns false on failure.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool RemoveWindowsServiceIfPresent(string name, bool announce)
    {
        ServiceController? controller = null;
        try
        {
            controller = new ServiceController(name);
            _ = controller.Status;
        }
        catch (InvalidOperationException)
        {
            controller?.Dispose();
            return true;
        }

        try
        {
            if (announce)
                Console.WriteLine($"Removing legacy service '{name}'...");

            if (controller.Status == ServiceControllerStatus.Running)
            {
                try
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to stop service '{name}': {ex.Message}");
                    return false;
                }
            }

            try
            {
                WindowsServiceManager.RemoveService(name);
                if (announce)
                    Console.WriteLine($"Service '{name}' removed.");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to remove service '{name}': {ex.Message}");
                return false;
            }
        }
        finally
        {
            controller.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static int UpdateWindows(ServiceController controller, string binPath, bool interactive)
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

        return StartWindows(interactive);
    }

    [SupportedOSPlatform("windows")]
    private static int FreshInstallWindows(string binPath, ParseResult parseResult, bool interactive)
    {
        var account = parseResult.GetValue(WorkerCliOptions.Account)
            ?? WindowsIdentity.GetCurrent().Name;

        Console.WriteLine($"Service will run as: {account}");

        string? password = null;

        if (!IsVirtualAccount(account))
        {
            if (!interactive)
                return LogonPasswordRequired(account);

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

        return StartWindows(interactive);
    }

    [SupportedOSPlatform("windows")]
    private static int StartWindows(bool interactive)
    {
        var started = TryStartService(out var logonFailure);
        if (started)
            return 0;
        if (!logonFailure)
            return 1;

        return RecoverFromLogonFailure(interactive);
    }

    /// <summary>Returns true when the service is running. Sets <paramref name="logonFailure"/> when Windows rejected the stored logon.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryStartService(out bool logonFailure)
    {
        logonFailure = false;
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                Console.WriteLine($"Service '{ServiceName}' installed and started successfully.");
                return true;
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service '{ServiceName}' installed and started successfully.");
            return true;
        }
        catch (Exception ex) when (ServiceLogonFailure.IsMatch(ex))
        {
            logonFailure = true;
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start service: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RecoverFromLogonFailure(bool interactive)
    {
        string account;
        try
        {
            account = WindowsServiceManager.QueryServiceStartName(ServiceName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start service: {ex.Message}");
            return 1;
        }

        if (IsVirtualAccount(account) || account.EndsWith('$'))
        {
            Console.Error.WriteLine("Failed to start service: the service did not start due to a logon failure.");
            return 1;
        }

        if (!interactive)
            return LogonPasswordRequired(account);

        Console.Error.WriteLine($"The Windows password for {account} was not accepted.");
        for (var attempt = 1; attempt <= PasswordAttempts; attempt++)
        {
            Console.Write($"Password for {account}: ");
            var password = ReadPasswordMasked();
            if (password.Length == 0)
            {
                Console.Error.WriteLine("Installation cancelled.");
                return 1;
            }

            try
            {
                var (domain, username) = ParseAccountName(account);
                if (!WindowsCredentialValidator.Validate(username, domain, password))
                {
                    Console.Error.WriteLine("Invalid credentials.");
                    continue;
                }

                Console.WriteLine("Updating the service logon password...");
                var ntAccount = new NTAccount(account);
                var sid = (SecurityIdentifier)ntAccount.Translate(typeof(SecurityIdentifier));
                WindowsLsaPolicy.GrantServiceLogonRight(sid);
                WindowsServiceManager.UpdateServicePassword(ServiceName, account, password);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to update the service password: {ex.Message}");
                return 1;
            }
            finally
            {
                password = new string('\0', password.Length);
            }

            var started = TryStartService(out var stillLogonFailure);
            if (started)
                return 0;
            if (!stillLogonFailure)
                return 1;

            Console.Error.WriteLine("That password was not accepted.");
        }

        return LogonPasswordRequired(account);
    }

    private static int LogonPasswordRequired(string account)
    {
        Console.Error.WriteLine(
            $"{LogonPasswordRequiredMarker}: The Windows password for {account} is no longer valid. Install the Worker again from GrayMoon and enter the current password.");
        return LogonPasswordRequiredExitCode;
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
            var domain = parts[0] is "." or "" ? Environment.MachineName : parts[0];
            return (domain, parts[1]);
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
        await RemoveSystemdUnitIfPresentAsync(LegacyServiceName, commandLine, cancellationToken, announce: true).ConfigureAwait(false);

        var unitPath = $"/etc/systemd/system/{ServiceName}.service";
        var unitContent = new StringBuilder();
        unitContent.AppendLine("[Unit]");
        unitContent.AppendLine("Description=GrayMoon Worker");
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

    internal static async Task RemoveSystemdUnitIfPresentAsync(
        string name,
        ICommandLineService commandLine,
        CancellationToken cancellationToken,
        bool announce)
    {
        var unitPath = $"/etc/systemd/system/{name}.service";
        if (!File.Exists(unitPath))
            return;

        if (announce)
            Console.WriteLine($"Removing legacy systemd unit '{name}'...");

        await commandLine.RunAsync("systemctl", $"disable {name}.service --now", null, null, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(unitPath);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot remove {unitPath}. Run with sudo.");
            return;
        }

        await commandLine.RunAsync("systemctl", "daemon-reload", null, null, cancellationToken).ConfigureAwait(false);
        if (announce)
            Console.WriteLine($"systemd unit '{name}' removed.");
    }
}
