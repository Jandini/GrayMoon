using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Jobs;

public sealed class CommandJob : ICommandJob
{
    public required string RequestId { get; init; }
    public required string Command { get; init; }
    public required object Request { get; init; }
}
