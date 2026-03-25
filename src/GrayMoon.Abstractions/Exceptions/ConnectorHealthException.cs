namespace GrayMoon.Abstractions.Exceptions;

/// <summary>
/// Thrown when a connector is unhealthy (inactive, token rejected, or token not set).
/// The <see cref="Exception.Message"/> property contains a human-readable description
/// suitable for display in the UI.
/// </summary>
public sealed class ConnectorHealthException : Exception
{
    public ConnectorHealthException(string message)
        : base(message)
    {
    }
}
