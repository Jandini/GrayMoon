using System.Diagnostics;
using System.Runtime.Versioning;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Windows lock diagnostics that cannot hang the Worker: a <see cref="WindowsLockScanner"/> pass (handle table, mapped files,
/// current directories) for all requested folders at once, run in the isolated <see cref="LockScanChildProcess"/> when
/// possible and otherwise on a background task the Worker stops waiting for at the deadline. When the handle table cannot be
/// read, Restart Manager (<see cref="WindowsFileLockInspector"/>) fills in. Read-only; the Worker's own process is never
/// reported.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHandleLockInspector(
    WindowsFileLockInspector restartManager,
    LockScanChildProcess childProcess,
    IOptions<WorkerOptions> options,
    ILogger<WindowsHandleLockInspector> logger) : IFileLockInspector
{
    /// <summary>How long past the budget an in-process scan may run before the Worker stops waiting for it.</summary>
    private static readonly TimeSpan InProcessGrace = TimeSpan.FromSeconds(2);

    public async Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
        => (await InspectManyAsync([path], cancellationToken))[0];

    public async Task<IReadOnlyList<FileLockInspectionResult>> InspectManyAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
            return [];

        var budget = options.Value.LockInspectionBudget;
        var started = Stopwatch.GetTimestamp();
        IReadOnlyCollection<int> excluded = [Environment.ProcessId];

        LockScanOutcome? outcome = null;
        var mode = "in-process";
        if (childProcess.IsAvailable)
        {
            var child = await childProcess.RunAsync(paths, excluded, budget, cancellationToken);
            switch (child.Status)
            {
                case LockScanChildProcess.ChildStatus.Completed:
                    outcome = child.Outcome;
                    mode = "isolated";
                    break;
                case LockScanChildProcess.ChildStatus.TimedOut:
                    return TimedOut(paths);
                default:
                    logger.LogWarning("Isolated lock scan failed, scanning in-process instead: {Error}", child.Error);
                    break;
            }
        }

        if (outcome is null)
        {
            try
            {
                outcome = await Task.Run(() => WindowsLockScanner.Scan(paths, excluded, budget, cancellationToken), cancellationToken)
                    .WaitAsync(budget + InProcessGrace, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("In-process lock scan exceeded {TimeoutMs}ms; it is abandoned", (long)(budget + InProcessGrace).TotalMilliseconds);
                return TimedOut(paths);
            }
        }

        var results = outcome.HandleTableAvailable
            ? outcome.Results
            : await WithRestartManagerAsync(paths, outcome, cancellationToken);

        logger.LogInformation(
            "Lock inspection ({Mode}) of {PathCount} path(s): {Count} blocking process(es), incomplete={Incomplete}, {ElapsedMs}ms ({Timings})",
            mode,
            paths.Count,
            results.Sum(r => r.Processes.Count),
            results.Any(r => r.MayBeIncomplete),
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            outcome.Timings);
        return results;
    }

    private static IReadOnlyList<FileLockInspectionResult> TimedOut(IReadOnlyList<string> paths) =>
        paths.Select(_ => new FileLockInspectionResult([], true, WindowsLockScanner.TimedOutDiagnostic)).ToList();

    /// <summary>The handle table was unavailable: add Restart Manager's open-file results to the scan's other passes.</summary>
    private async Task<IReadOnlyList<FileLockInspectionResult>> WithRestartManagerAsync(
        IReadOnlyList<string> paths,
        LockScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Handle table unavailable ({Error}); using Restart Manager for open files", outcome.HandleTableError);
        var merged = new List<FileLockInspectionResult>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            var scanned = outcome.Results[i];
            if (!Directory.Exists(paths[i]) && !File.Exists(paths[i]))
            {
                merged.Add(scanned);
                continue;
            }

            var fallback = await restartManager.InspectAsync(paths[i], cancellationToken);
            var byProcessId = scanned.Processes.ToDictionary(p => p.ProcessId);
            foreach (var process in fallback.Processes)
                byProcessId.TryAdd(process.ProcessId, WindowsProcessClassifier.Enrich(process));

            var diagnostics = new[] { scanned.Diagnostic, fallback.Diagnostic }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList();
            merged.Add(new FileLockInspectionResult(
                byProcessId.Values
                    .OrderBy(p => p.ProcessName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.ProcessId)
                    .ToList(),
                scanned.MayBeIncomplete || fallback.MayBeIncomplete,
                diagnostics.Count == 0 ? null : string.Join(" ", diagnostics)));
        }

        return merged;
    }
}
