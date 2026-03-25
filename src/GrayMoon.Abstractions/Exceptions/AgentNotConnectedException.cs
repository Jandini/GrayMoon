namespace GrayMoon.Abstractions.Exceptions;

/// <summary>
/// Thrown when an operation requires the GrayMoon Agent to be connected but it is not.
/// </summary>
public sealed class AgentNotConnectedException : Exception
{
    public AgentNotConnectedException()
        : base("The GrayMoon Agent is not connected. Start the Agent and try again.")
    {
    }

    public AgentNotConnectedException(string message)
        : base(message)
    {
    }
}
