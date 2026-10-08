using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Ends the blocking processes the user selected in the Remove Feature dialog ("Kill and remove" / "Kill and retry"). Never
/// trusts the selection on its own: the Worker inspects the Feature folders again and ends a process only when it still holds
/// one of them now, still has the start time the user saw (so a reused process id is never ended), and is not protected
/// (system, service, Explorer, GrayMoon, or no right to end it). Returns one outcome per selected process and a fresh
/// inspection of every path.
/// </summary>
public sealed class TerminateBlockingProcessesCommand(
    IFileLockInspector lockInspector,
    IProcessTerminator terminator,
    ILogger<TerminateBlockingProcessesCommand> logger)
    : ICommandHandler<TerminateBlockingProcessesRequest, TerminateBlockingProcessesResponse>
{
    internal const int MaxProcesses = 200;

    private static readonly TimeSpan WaitForExit = TimeSpan.FromSeconds(3);

    public async Task<TerminateBlockingProcessesResponse> ExecuteAsync(
        TerminateBlockingProcessesRequest request,
        CancellationToken cancellationToken = default)
    {
        var paths = request.Paths ?? [];
        var selections = request.Processes ?? [];
        if (paths.Count == 0)
            return Fail("paths required");
        if (paths.Count > InspectPathLocksCommand.MaxPaths)
            return Fail($"At most {InspectPathLocksCommand.MaxPaths} paths can be used at once.");
        if (paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
            return Fail("Every path must be absolute.");
        if (selections.Count == 0)
            return Fail("processes required");
        if (selections.Count > MaxProcesses)
            return Fail($"At most {MaxProcesses} processes can be ended at once.");

        var before = await InspectPathLocksCommand.InspectAsync(lockInspector, paths, logger, cancellationToken);
        var holders = new Dictionary<int, (BlockingProcessResponse Process, string? Path)>();
        foreach (var result in before)
        {
            foreach (var process in result.BlockingProcesses ?? [])
                holders.TryAdd(process.ProcessId, (process, result.Path));
        }

        var outcomes = new List<TerminateProcessOutcomeResponse>();
        foreach (var selection in selections.DistinctBy(s => s.ProcessId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!holders.TryGetValue(selection.ProcessId, out var holder))
            {
                outcomes.Add(Outcome(selection.ProcessId, null, terminator.IsRunning(selection.ProcessId)
                    ? TerminateProcessOutcome.NotHoldingAnymore
                    : TerminateProcessOutcome.AlreadyExited));
                continue;
            }

            var process = holder.Process;
            outcomes.Add(Outcome(process.ProcessId, process.ProcessName, Decide(selection, process, holder.Path)));
        }

        var after = await InspectPathLocksCommand.InspectAsync(lockInspector, paths, logger, cancellationToken);
        return new TerminateBlockingProcessesResponse { Success = true, Outcomes = outcomes, Results = after };
    }

    private string Decide(TerminateProcessSelection selection, BlockingProcessResponse process, string? path)
    {
        if (selection.StartTimeUtc is null || process.StartTimeUtc is null || selection.StartTimeUtc.Value != process.StartTimeUtc.Value)
            return TerminateProcessOutcome.StartTimeChanged;

        if (!process.CanTerminate)
        {
            return process.ProtectedReason == BlockingProcessProtectedReason.AccessDenied
                ? TerminateProcessOutcome.AccessDenied
                : TerminateProcessOutcome.Protected;
        }

        logger.LogInformation(
            "Ending process {ProcessId} ({ProcessName}, {ExecutablePath}) that holds {Path}, as the user confirmed in Remove Feature",
            process.ProcessId, process.ProcessName, process.ExecutablePath, path);
        var outcome = terminator.Terminate(process.ProcessId, process.StartTimeUtc.Value, WaitForExit);
        if (outcome != TerminateProcessOutcome.Killed)
            logger.LogWarning("Process {ProcessId} ({ProcessName}) was not ended: {Outcome}", process.ProcessId, process.ProcessName, outcome);
        return outcome;
    }

    private static TerminateProcessOutcomeResponse Outcome(int processId, string? processName, string outcome) =>
        new() { ProcessId = processId, ProcessName = processName, Outcome = outcome };

    private static TerminateBlockingProcessesResponse Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
