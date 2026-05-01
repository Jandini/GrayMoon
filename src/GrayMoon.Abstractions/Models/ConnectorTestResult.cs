namespace GrayMoon.Abstractions.Models;

/// <summary>
/// Result of a connector connection test. Carries success state and a human-readable error
/// message when the test fails, suitable for display in the UI.
/// </summary>
public sealed record ConnectorTestResult(bool Success, string? ErrorMessage = null, bool IsConnectorFault = false)
{
    public static ConnectorTestResult Ok() => new(true);

    /// <summary>
    /// Transient failure (network outage, rate limit, server down). The connector stays active.
    /// </summary>
    public static ConnectorTestResult Fail(string error) => new(false, error);

    /// <summary>
    /// Configuration fault (invalid/expired token, missing token, wrong API base URL).
    /// The connector should be deactivated until the user corrects the configuration.
    /// </summary>
    public static ConnectorTestResult Fault(string error) => new(false, error, IsConnectorFault: true);
}
