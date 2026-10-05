using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Jobs;

public sealed class CommandJob : ICommandJob
{
    public required string RequestId { get; init; }
    public required string Command { get; init; }
    public required object Request { get; init; }
}
