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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int UninstallWindows()
    {
        var currentOk = InstallCommandHandler.RemoveWindowsServiceIfPresent(InstallCommandHandler.ServiceName, announce: true);
        var legacyOk = InstallCommandHandler.RemoveWindowsServiceIfPresent(InstallCommandHandler.LegacyServiceName, announce: true);
        return currentOk && legacyOk ? 0 : 1;
    }

    private static async Task<int> UninstallSystemdAsync(CancellationToken cancellationToken, ICommandLineService commandLine)
    {
        await InstallCommandHandler.RemoveSystemdUnitIfPresentAsync(
            InstallCommandHandler.ServiceName, commandLine, cancellationToken, announce: true).ConfigureAwait(false);
        await InstallCommandHandler.RemoveSystemdUnitIfPresentAsync(
            InstallCommandHandler.LegacyServiceName, commandLine, cancellationToken, announce: true).ConfigureAwait(false);
        return 0;
    }
}
