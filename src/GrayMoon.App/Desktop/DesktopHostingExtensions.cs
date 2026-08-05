namespace GrayMoon.App.Desktop;

/// <summary>
/// Extension methods that reconfigure the web host for desktop mode.
/// Called only when GrayMoon.App is launched with --desktop &lt;pipe-name&gt;.
/// </summary>
public static class DesktopHostingExtensions
{
    /// <summary>
    /// Fixed loopback port used in desktop mode. A stable port (rather than an OS-assigned
    /// ephemeral one) is required so an installed GrayMoon Agent Windows Service - whose
    /// --hub-url is baked into its static service command line at install time - keeps
    /// working across GrayMoon.App/Desktop restarts and machine reboots without needing to
    /// be reinstalled. Matches the port already used by the App's Docker/dev-mode hosting.
    /// </summary>
    public const int DesktopPort = 8384;

    /// <summary>
    /// Process exit code Program.cs returns when startup fails because the desktop-mode
    /// listen address (127.0.0.1:<see cref="DesktopPort"/>) is already in use (i.e. an
    /// <see cref="Microsoft.AspNetCore.Connections.AddressInUseException"/> anywhere in the
    /// startup exception chain). GrayMoon.Desktop's AppProcessManager checks for this specific
    /// code to show a precise, human-friendly error instead of a generic "process exited"
    /// message - keep the two in sync if this value ever changes.
    /// </summary>
    public const int AddressInUseExitCode = 62;

    /// <summary>
    /// Configures Kestrel to listen only on 127.0.0.1 on the fixed <see cref="DesktopPort"/>.
    /// The actual bound URL is reported to the desktop process via the startup named pipe.
    /// </summary>
    public static IWebHostBuilder UseDesktopMode(
        this IWebHostBuilder builder,
        string pipeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(pipeName);

        // Bind only to loopback, on a fixed port so an installed Agent service's baked-in
        // hub URL stays valid across restarts. Single-instance is enforced by GrayMoon.Desktop's
        // SingleInstanceService, so only one GrayMoon.App process should ever bind this port.
        builder.UseUrls($"http://127.0.0.1:{DesktopPort}");

        // Store the pipe name in configuration so DesktopStartupService can read it
        builder.UseSetting("Desktop:PipeName", pipeName);

        return builder;
    }
}
