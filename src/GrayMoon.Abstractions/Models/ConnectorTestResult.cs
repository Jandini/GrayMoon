namespace GrayMoon.Abstractions.Models;

/// <summary>
/// Result of a connector connection test. Carries success state and a human-readable error
/// message when the test fails, suitable for display in the UI.
/// </summary>
public sealed record ConnectorTestResult(bool Success, string? ErrorMessage = null)
{
    public static ConnectorTestResult Ok() => new(true);
    public static ConnectorTestResult Fail(string error) => new(false, error);
}
