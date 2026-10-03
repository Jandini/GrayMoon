namespace GrayMoon.App.Services.Security;

/// <summary>
/// Local-network security settings (F3). No user login exists in GrayMoon; these settings let
/// <see cref="RequestSecurityMiddleware"/> tell a same-host request (the Desktop WebView, a normal
/// browser tab on GrayMoon, the Worker, scripts) apart from a cross-site request coming from some
/// other page open in the same browser.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Extra hostnames (no scheme, no port) that count as "this app's own host" in addition to the
    /// request's own <c>Host</c> header and <c>X-Forwarded-Host</c> when present. Lets a Docker user
    /// behind a reverse proxy allow their public hostname without opening GrayMoon to every site.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];
}
