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
    /// Extra hostnames (no scheme, no port) a browser page may be served from, in addition to loopback
    /// names and the hosts listed in <c>AllowedHosts</c> (<c>GRAYMOON_AllowedHosts</c>). The request's own
    /// <c>Host</c> and <c>X-Forwarded-Host</c> headers never count (DNS rebinding sends the attacker's name
    /// in both). Lets a Docker user behind a reverse proxy allow their public hostname.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>
    /// When true (the default), the Worker hub and the Worker capabilities endpoint reject a Worker that
    /// presents no secret (F2), from the first start. Setting it to false is an explicit opt-out for a
    /// Worker installed before secrets existed: such a Worker is then accepted with a warning until some
    /// Worker has proved it has the secret. The connector token endpoint ignores this setting and always
    /// requires the secret; see <see cref="WorkerSecretMiddleware"/>.
    /// </summary>
    public bool RequireWorkerSecret { get; init; } = true;
}
