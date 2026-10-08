using System.Diagnostics;
using System.Runtime.Versioning;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Platform.Windows;

namespace GrayMoon.Worker.Services;

/// <summary>
/// One lock scan over several folders, in three passes whose cost does not depend on how many files the folders hold:
/// <list type="number">
/// <item>the system handle table: open files and folders (<see cref="WindowsHandleTable"/>);</item>
/// <item>each process's mapped files: programs and libraries running from a folder (<see cref="WindowsMappedFiles"/>);</item>
/// <item>each process's current directory: shells and AI tools sitting in a folder.</item>
/// </list>
/// Every hit is attributed to the most specific inspected folder. Bounded by a time budget: whatever was found when it runs out
/// is returned and marked incomplete. Read-only; runs inside the isolated <c>inspect-locks</c> child process when it can.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLockScanner
{
    internal static readonly TimeSpan PerHandleLimit = TimeSpan.FromMilliseconds(250);

    internal const string TimedOutDiagnostic = "The check took too long and may have missed some programs.";
    internal const string SlowHandlesDiagnostic = "Some open files could not be checked in time.";

    internal static LockScanOutcome Scan(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<int> excludeProcessIds,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + budget;
        var roots = paths.Select(ScanRoot.Create).ToList();
        if (roots.All(r => !r.Exists))
            return new LockScanOutcome(roots.Select(r => r.NotScannedResult).ToList(), true, null);

        var rootForms = roots.Select(r => r.Exists ? r.Forms : (IReadOnlyList<string>)[]).ToList();
        var skip = new HashSet<int>(excludeProcessIds) { 0, 4, Environment.ProcessId };
        var hits = new Dictionary<(int Root, int ProcessId), BlockingProcessReason>();
        var diagnostics = new List<string>();
        var incomplete = false;

        void Hit(string path, int processId, BlockingProcessReason reason)
        {
            var root = BlockingProcessRules.MostSpecificRoot(rootForms, path);
            if (root < 0)
                return;

            var key = (root, processId);
            if (!hits.TryGetValue(key, out var existing) || Rank(reason) > Rank(existing))
                hits[key] = reason;
        }

        void Incomplete(string diagnostic)
        {
            incomplete = true;
            if (!diagnostics.Contains(diagnostic))
                diagnostics.Add(diagnostic);
        }

        var processIds = RunningProcessIds(skip);
        var timings = new List<string>();
        var passStarted = Stopwatch.GetTimestamp();

        void EndPass(string name)
        {
            timings.Add($"{name} {(long)Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds}ms");
            passStarted = Stopwatch.GetTimestamp();
        }

        bool OutOfTime()
        {
            if (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
                return false;
            Incomplete(TimedOutDiagnostic);
            return true;
        }

        // Cheapest and most telling first, so a scan that runs out of time still names the usual blockers.
        // Pass 1: current directories (shells and AI tools sitting in a folder).
        if (!Environment.Is64BitProcess)
            Incomplete("Programs whose current folder is inside it could not be checked.");
        foreach (var processId in processIds)
        {
            if (OutOfTime())
                break;

            var currentDirectory = WindowsProcessInspector.TryGetCurrentDirectory(processId);
            if (currentDirectory is not null)
                Hit(currentDirectory, processId, BlockingProcessReason.WorkingDirectory);
        }

        EndPass("cwd");

        // Pass 2: open files and folders, from the handle table.
        var handles = WindowsHandleTable.Scan(skip, deadline, PerHandleLimit, cancellationToken);
        foreach (var (processId, path) in handles.Paths)
            Hit(path, processId, BlockingProcessReason.OpenFile);
        if (handles.SlowHandlesSkipped > 0)
            Incomplete(SlowHandlesDiagnostic);
        if (handles.TimedOut)
            Incomplete(TimedOutDiagnostic);
        EndPass("handles");

        // Pass 3: mapped files (programs and libraries running from a folder).
        if (!handles.TimedOut)
        {
            var deviceMap = WindowsPathNames.BuildDeviceMap();
            foreach (var processId in processIds)
            {
                if (OutOfTime())
                    break;

                var mapped = WindowsMappedFiles.TryGetMappedFilePaths(processId, deviceMap, deadline);
                if (mapped is null)
                    continue;

                foreach (var path in mapped)
                    Hit(path, processId, BlockingProcessReason.LoadedModule);
            }

            EndPass("mapped");
        }

        var described = new Dictionary<int, BlockingProcessInfo?>();
        BlockingProcessInfo? Describe(int processId)
        {
            if (!described.TryGetValue(processId, out var info))
            {
                info = WindowsProcessClassifier.Describe(processId);
                described[processId] = info;
            }

            return info;
        }

        var diagnostic = diagnostics.Count == 0 ? null : string.Join(" ", diagnostics);
        var results = roots.Select((root, index) =>
        {
            if (!root.Exists)
                return root.NotScannedResult;

            var found = hits
                .Where(h => h.Key.Root == index)
                .Select(h => Describe(h.Key.ProcessId) is { } info ? info with { Reason = h.Value } : null)
                .OfType<BlockingProcessInfo>()
                .OrderBy(p => p.ProcessName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.ProcessId)
                .ToList();
            return new FileLockInspectionResult(found, incomplete, diagnostic);
        }).ToList();

        EndPass("describe");
        return new LockScanOutcome(results, handles.Available, handles.Error, string.Join(", ", timings));
    }

    private static List<int> RunningProcessIds(IReadOnlySet<int> skip)
    {
        var ids = new List<int>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return ids;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!skip.Contains(process.Id))
                        ids.Add(process.Id);
                }
                catch
                {
                    // Exited while listing.
                }
            }
        }

        return ids;
    }

    /// <summary>The most useful reason wins when a process holds a folder in several ways.</summary>
    private static int Rank(BlockingProcessReason reason) => reason switch
    {
        BlockingProcessReason.WorkingDirectory => 2,
        BlockingProcessReason.OpenFile => 1,
        _ => 0,
    };

    /// <summary>One inspected path: whether it exists, and its spellings (as requested, and with links resolved).</summary>
    private sealed record ScanRoot(bool Exists, IReadOnlyList<string> Forms, FileLockInspectionResult NotScannedResult)
    {
        public static ScanRoot Create(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                return new ScanRoot(false, [], new FileLockInspectionResult([], true, $"The path could not be resolved: {ex.Message}"));
            }

            if (!Directory.Exists(full) && !File.Exists(full))
                return new ScanRoot(false, [], FileLockInspectionResult.Empty);

            var forms = new List<string> { full };
            var resolved = WindowsPathNames.TryResolveFinalPath(full)?.TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, full, StringComparison.OrdinalIgnoreCase))
                forms.Add(resolved);

            return new ScanRoot(true, forms, FileLockInspectionResult.Empty);
        }
    }
}

/// <summary>Names and classifies a blocking process, and decides whether GrayMoon may end it.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessClassifier
{
    /// <summary>Describes a running process found by a scan; null when it has already exited.</summary>
    internal static BlockingProcessInfo? Describe(int processId)
    {
        string? name = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch
        {
            // Exited, or not readable; the executable path may still name it.
        }

        var executablePath = WindowsProcessInspector.TryGetExecutablePath(processId);
        if (name is null && executablePath is null)
            return null;

        name ??= Path.GetFileNameWithoutExtension(executablePath);
        var info = new BlockingProcessInfo(
            processId,
            name,
            executablePath,
            ServiceName: null,
            KindOf(processId, name),
            BlockingProcessReason.OpenFile,
            WindowsProcessInspector.TryGetStartTimeUtc(processId));
        return Protect(info);
    }

    /// <summary>Adds identity and protection to a process Restart Manager reported (keeps its kind and service name).</summary>
    internal static BlockingProcessInfo Enrich(BlockingProcessInfo info)
    {
        var kind = info.Kind == BlockingProcessKind.Unknown ? KindOf(info.ProcessId, info.ProcessName) : info.Kind;
        if (BlockingProcessRules.IsSystemProcess(info.ProcessId, info.ProcessName))
            kind = BlockingProcessKind.Critical;

        return Protect(info with
        {
            Kind = kind,
            StartTimeUtc = info.StartTimeUtc ?? WindowsProcessInspector.TryGetStartTimeUtc(info.ProcessId),
        });
    }

    private static BlockingProcessKind KindOf(int processId, string? name)
    {
        if (BlockingProcessRules.IsSystemProcess(processId, name))
            return BlockingProcessKind.Critical;
        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
            return BlockingProcessKind.Explorer;
        if (WindowsProcessInspector.TryGetSessionId(processId) == 0)
            return BlockingProcessKind.Service;
        return BlockingProcessKind.Application;
    }

    private static BlockingProcessInfo Protect(BlockingProcessInfo info)
    {
        var reason = BlockingProcessRules.FixedProtection(info);
        if (reason is null && !WindowsProcessInspector.CanOpenForTerminate(info.ProcessId))
            reason = BlockingProcessProtectedReason.AccessDenied;
        return BlockingProcessRules.WithProtection(info, reason);
    }
}
