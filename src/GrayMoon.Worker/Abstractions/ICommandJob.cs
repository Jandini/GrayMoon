namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Job from SignalR RequestCommand: has request ID and typed request; requires ResponseCommand back.
/// </summary>
public interface ICommandJob : IJob
{
    string RequestId { get; }
    string Command { get; }
    object Request { get; }

    /// <summary>Optional parent App operation id from <c>RequestCommand</c>; null when the App sent none.</summary>
    string? OperationId { get; }
}
