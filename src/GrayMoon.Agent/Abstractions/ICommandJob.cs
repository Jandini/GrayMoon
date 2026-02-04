namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Job from SignalR RequestCommand: has request ID and typed request; requires ResponseCommand back.
/// </summary>
public interface ICommandJob : IJob
{
    string RequestId { get; }
    string Command { get; }
    object Request { get; }
}
