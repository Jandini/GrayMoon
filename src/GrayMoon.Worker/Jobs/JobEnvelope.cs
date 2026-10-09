using System.Diagnostics;
using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Jobs;

/// <summary>
/// Payload carried by the queue: discriminator plus one job (command or notify).
/// </summary>
public sealed class JobEnvelope
{
    public JobKind Kind { get; init; }
    public ICommandJob? CommandJob { get; init; }
    public INotifyJob? NotifyJob { get; init; }

    /// <summary>Stopwatch timestamp taken when the envelope was created (at enqueue); used to log queue wait.</summary>
    public long EnqueuedTimestamp { get; } = Stopwatch.GetTimestamp();

    public static JobEnvelope Command(ICommandJob job) =>
        new() { Kind = JobKind.Command, CommandJob = job };

    public static JobEnvelope Notify(INotifyJob job) =>
        new() { Kind = JobKind.Notify, NotifyJob = job };
}
