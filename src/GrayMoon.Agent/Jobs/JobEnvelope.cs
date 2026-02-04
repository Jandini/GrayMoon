using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Jobs;

/// <summary>
/// Payload carried by the queue: discriminator plus one job (command or notify).
/// </summary>
public sealed class JobEnvelope
{
    public JobKind Kind { get; init; }
    public ICommandJob? CommandJob { get; init; }
    public INotifyJob? NotifyJob { get; init; }

    public static JobEnvelope Command(ICommandJob job) =>
        new() { Kind = JobKind.Command, CommandJob = job };

    public static JobEnvelope Notify(INotifyJob job) =>
        new() { Kind = JobKind.Notify, NotifyJob = job };
}
