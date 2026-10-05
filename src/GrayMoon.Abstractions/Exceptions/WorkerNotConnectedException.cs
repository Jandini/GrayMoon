namespace GrayMoon.Abstractions.Exceptions;

/// <summary>
/// Thrown when an operation requires the GrayMoon Worker to be connected but it is not.
/// </summary>
public sealed class WorkerNotConnectedException : Exception
{
    public WorkerNotConnectedException()
        : base("The GrayMoon Worker is not connected. Start the Worker and try again.")
    {
    }

    public WorkerNotConnectedException(string message)
        : base(message)
    {
    }
}
