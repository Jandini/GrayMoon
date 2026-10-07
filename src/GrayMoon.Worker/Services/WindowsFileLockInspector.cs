using System.Diagnostics;
using System.Runtime.Versioning;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Windows lock diagnostics in two layers:
/// <list type="number">
/// <item>Restart Manager over the files still under the path (an editor, a build, a test runner, Explorer, a scanner).</item>
/// <item>A current-directory scan of every readable process: a shell or an AI coding tool sitting in the folder keeps the
/// folder itself in use without having any file open, which Restart Manager cannot see.</item>
/// </list>
/// Read-only and only meant for the failure path; the Worker's own process is never reported.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileLockInspector(ILogger<WindowsFileLockInspector> logger) : IFileLockInspector
{
    /// <summary>At most this many files are handed to Restart Manager; more means the result may be incomplete.</summary>
    internal const int MaxFilesToRegister = 4000;

    private const int RegisterBatchSize = 500;

    public Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    private FileLockInspectionResult Inspect(string path, CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            return new FileLockInspectionResult([], true, $"The path could not be resolved: {ex.Message}");
        }

        var isDirectory = Directory.Exists(root);
        if (!isDirectory && !File.Exists(root))
            return FileLockInspectionResult.Empty;

        var started = Stopwatch.GetTimestamp();
        var ownProcessId = Environment.ProcessId;
        var byProcessId = new Dictionary<int, BlockingProcessInfo>();
        var diagnostics = new List<string>();
        var incomplete = false;

        // Layer 1: Restart Manager over the files.
        var (files, truncated) = isDirectory ? CollectFiles(root, MaxFilesToRegister) : ([root], false);
        if (truncated)
        {
            incomplete = true;
            diagnostics.Add($"Only the first {MaxFilesToRegister} files were checked.");
        }

        try
        {
            var (rmProcesses, rmError) = WindowsRestartManager.GetProcessesUsingFiles(files, RegisterBatchSize, cancellationToken);
            if (rmError != null)
            {
                incomplete = true;
                diagnostics.Add(rmError);
                logger.LogWarning("Lock inspection for {Path}: {Error}", root, rmError);
            }

            foreach (var process in rmProcesses)
            {
                if (process.ProcessId == ownProcessId || byProcessId.ContainsKey(process.ProcessId))
                    continue;

                var executablePath = WindowsProcessInspector.TryGetExecutablePath(process.ProcessId);
                byProcessId[process.ProcessId] = new BlockingProcessInfo(
                    process.ProcessId,
                    process.AppName ?? NameFromExecutable(executablePath) ?? TryGetProcessName(process.ProcessId),
                    executablePath,
                    process.Kind == BlockingProcessKind.Service ? process.ServiceName : null,
                    process.Kind,
                    BlockingProcessReason.OpenFile);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            incomplete = true;
            diagnostics.Add("Open files could not be checked.");
            logger.LogWarning(ex, "Restart Manager inspection failed for {Path}", root);
        }

        // Layer 2: processes whose current directory is inside the folder.
        if (isDirectory)
        {
            if (!Environment.Is64BitProcess)
            {
                incomplete = true;
                diagnostics.Add("Programs whose current folder is inside it could not be checked.");
            }
            else
            {
                foreach (var info in FindProcessesWithWorkingDirectoryUnder(root, ownProcessId, byProcessId.Keys, cancellationToken))
                    byProcessId[info.ProcessId] = info;
            }
        }

        var processes = byProcessId.Values
            .OrderBy(p => p.ProcessName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

        logger.LogInformation(
            "Lock inspection for {Path}: {Count} blocking process(es), {FileCount} file(s) checked, incomplete={Incomplete}, {ElapsedMs}ms",
            root, processes.Count, files.Count, incomplete, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new FileLockInspectionResult(processes, incomplete, diagnostics.Count == 0 ? null : string.Join(" ", diagnostics));
    }

    private static IEnumerable<BlockingProcessInfo> FindProcessesWithWorkingDirectoryUnder(
        string root,
        int ownProcessId,
        IEnumerable<int> alreadyFound,
        CancellationToken cancellationToken)
    {
        var skip = new HashSet<int>(alreadyFound) { ownProcessId, 0, 4 };
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            yield break;
        }

        foreach (var process in all)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int processId;
                try
                {
                    processId = process.Id;
                }
                catch
                {
                    continue;
                }

                if (skip.Contains(processId))
                    continue;

                var currentDirectory = WindowsProcessInspector.TryGetCurrentDirectory(processId);
                if (currentDirectory is null || !IsSameOrUnder(currentDirectory, root))
                    continue;

                var executablePath = WindowsProcessInspector.TryGetExecutablePath(processId);
                string? name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    name = NameFromExecutable(executablePath);
                }

                yield return new BlockingProcessInfo(
                    processId,
                    name,
                    executablePath,
                    ServiceName: null,
                    BlockingProcessKind.Application,
                    BlockingProcessReason.WorkingDirectory);
            }
        }
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or a path under it (case-insensitive, trailing separators ignored).</summary>
    internal static bool IsSameOrUnder(string candidate, string root)
    {
        string normalizedCandidate;
        string normalizedRoot;
        try
        {
            normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Files under <paramref name="root"/>, never entering a junction or symlink, up to <paramref name="max"/>.</summary>
    internal static (List<string> Files, bool Truncated) CollectFiles(string root, int max)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    if (entry is DirectoryInfo)
                    {
                        if (!isReparsePoint)
                            pending.Push(entry.FullName);
                        continue;
                    }

                    if (files.Count >= max)
                        return (files, true);
                    files.Add(entry.FullName);
                }
            }
            catch
            {
                // A folder that disappears or becomes unreadable mid-walk is skipped.
            }
        }

        return (files, false);
    }

    private static string? NameFromExecutable(string? executablePath) =>
        string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFileNameWithoutExtension(executablePath);

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
