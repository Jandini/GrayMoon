namespace GrayMoon.App.Desktop;

/// <summary>
/// Extension methods that reconfigure the web host for desktop mode.
/// Called only when GrayMoon.App is launched with --desktop &lt;pipe-name&gt;.
/// </summary>
public static class DesktopHostingExtensions
{
    /// <summary>
    /// Configures Kestrel to listen only on 127.0.0.1 with an OS-assigned port (port 0).
    /// The actual bound URL is reported to the desktop process via the startup named pipe.
    /// </summary>
    public static IWebHostBuilder UseDesktopMode(
        this IWebHostBuilder builder,
        string pipeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(pipeName);

        // Bind only to loopback; port 0 lets the OS assign a free port
        builder.UseUrls("http://127.0.0.1:0");

        // Store the pipe name in configuration so DesktopStartupService can read it
        builder.UseSetting("Desktop:PipeName", pipeName);

        return builder;
    }
}
