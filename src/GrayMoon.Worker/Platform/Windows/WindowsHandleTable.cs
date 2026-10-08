using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>
/// Lists the files and folders other processes have open, from one snapshot of the system handle table
/// (<c>NtQuerySystemInformation(SystemExtendedHandleInformation)</c>, the same source Sysinternals Handle uses). Cost depends on
/// the number of open handles in the system, not on how many files a folder holds, and it sees folder handles (Explorer windows,
/// file watchers, a shell's current folder) that Restart Manager cannot.
/// <para>
/// Hang safety: only handles of the File object type are considered, and each one must be a disk file
/// (<c>GetFileType == FILE_TYPE_DISK</c>) before its name is queried, so synchronous pipes are never named. Naming runs on a
/// background thread watched by the caller: a handle that takes longer than the per-handle limit is skipped and naming
/// continues on a fresh thread, and the whole pass stops at the deadline. Read-only: handles are duplicated into this process
/// only to read their names and closed straight away.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHandleTable
{
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x0002;
    private const uint FileTypeDisk = 0x0001;
    private const int MaxBufferBytes = 1024 * 1024 * 1024;

    // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX (x64): Object, UniqueProcessId, HandleValue, GrantedAccess, CreatorBackTraceIndex,
    // ObjectTypeIndex, HandleAttributes, Reserved.
    private const int EntrySize = 40;
    private const int EntryProcessIdOffset = 8;
    private const int EntryHandleOffset = 16;
    private const int EntryObjectTypeOffset = 30;

    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Paths other processes hold open. <see cref="Available"/> is false when the handle table could not be read at all.</summary>
    internal sealed record HandleScan(
        IReadOnlyList<(int ProcessId, string Path)> Paths,
        bool Available,
        int SlowHandlesSkipped,
        bool TimedOut,
        string? Error);

    private readonly record struct HandleEntry(int ProcessId, IntPtr Handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    /// <summary>
    /// Reads the open file and folder handles of every process except <paramref name="skipProcessIds"/> and returns their
    /// paths. Never throws; never runs past <paramref name="deadlineUtc"/> (plus one watch interval).
    /// </summary>
    internal static HandleScan Scan(
        IReadOnlySet<int> skipProcessIds,
        DateTime deadlineUtc,
        TimeSpan perHandleLimit,
        CancellationToken cancellationToken)
    {
        if (!Environment.Is64BitProcess)
            return new HandleScan([], false, 0, false, "The handle table can only be read by a 64-bit Worker.");

        List<HandleEntry> entries;
        try
        {
            var (snapshot, error) = ReadFileHandles(skipProcessIds);
            if (snapshot is null)
                return new HandleScan([], false, 0, false, error);
            entries = snapshot;
        }
        catch (Exception ex)
        {
            return new HandleScan([], false, 0, false, $"The handle table could not be read: {ex.Message}");
        }

        var paths = NameHandles(entries, deadlineUtc, perHandleLimit, cancellationToken, out var skipped, out var timedOut);
        return new HandleScan(paths, true, skipped, timedOut, null);
    }

    private static (List<HandleEntry>? Entries, string? Error) ReadFileHandles(IReadOnlySet<int> skipProcessIds)
    {
        // A file this process holds open while the snapshot is taken tells which object type index means "File" on this
        // Windows build (the index is not fixed across versions).
        var probePath = Path.Combine(Path.GetTempPath(), $"graymoon-lock-probe-{Guid.NewGuid():N}.tmp");
        using var probe = File.OpenHandle(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
        var probeHandle = probe.DangerousGetHandle();
        var ownProcessId = Environment.ProcessId;

        var size = 8 * 1024 * 1024;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    // The table can grow between calls; leave headroom.
                    size = Math.Max(needed, size) + (4 * 1024 * 1024);
                    if (size > MaxBufferBytes)
                        return (null, "The handle table is too large to read.");
                    continue;
                }

                if (status != 0)
                    return (null, $"The handle table could not be read (status 0x{status:X8}).");

                var count = (long)Marshal.ReadIntPtr(buffer);
                var first = buffer + (2 * IntPtr.Size);
                var maxCount = (size - (2 * IntPtr.Size)) / EntrySize;
                if (count < 0 || count > maxCount)
                    return (null, "The handle table snapshot was not readable.");

                ushort? fileType = null;
                for (long i = 0; i < count; i++)
                {
                    var entry = first + (nint)(i * EntrySize);
                    if ((long)Marshal.ReadIntPtr(entry + EntryProcessIdOffset) == ownProcessId
                        && Marshal.ReadIntPtr(entry + EntryHandleOffset) == probeHandle)
                    {
                        fileType = (ushort)Marshal.ReadInt16(entry + EntryObjectTypeOffset);
                        break;
                    }
                }

                if (fileType is null)
                    return (null, "The file object type could not be identified.");

                var result = new List<HandleEntry>();
                for (long i = 0; i < count; i++)
                {
                    var entry = first + (nint)(i * EntrySize);
                    if ((ushort)Marshal.ReadInt16(entry + EntryObjectTypeOffset) != fileType.Value)
                        continue;

                    var processId = (int)(long)Marshal.ReadIntPtr(entry + EntryProcessIdOffset);
                    if (processId == ownProcessId || skipProcessIds.Contains(processId))
                        continue;

                    result.Add(new HandleEntry(processId, Marshal.ReadIntPtr(entry + EntryHandleOffset)));
                }

                // Grouped by process so each source process is opened once and a process we cannot open is skipped quickly.
                result.Sort((a, b) => a.ProcessId.CompareTo(b.ProcessId));
                return (result, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>One naming thread's progress, read by the watching thread.</summary>
    private sealed class NamingRun
    {
        public volatile bool Abandoned;
        public int CurrentIndex = -1;
        public long CurrentStarted;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private static List<(int ProcessId, string Path)> NameHandles(
        List<HandleEntry> entries,
        DateTime deadlineUtc,
        TimeSpan perHandleLimit,
        CancellationToken cancellationToken,
        out int skipped,
        out bool timedOut)
    {
        var results = new List<(int ProcessId, string Path)>();
        var sourceProcesses = new ConcurrentDictionary<int, IntPtr>();
        skipped = 0;
        timedOut = false;

        try
        {
            var start = 0;
            while (start < entries.Count)
            {
                var run = new NamingRun();
                var from = start;
                var thread = new Thread(() => NameRange(entries, from, run, sourceProcesses, results))
                {
                    IsBackground = true,
                    Name = "graymoon-lock-scan",
                };
                thread.Start();

                var restartAt = -1;
                while (!run.Done.Wait(WatchInterval))
                {
                    if (cancellationToken.IsCancellationRequested || DateTime.UtcNow >= deadlineUtc)
                    {
                        run.Abandoned = true;
                        timedOut = true;
                        return Snapshot(results);
                    }

                    var index = Volatile.Read(ref run.CurrentIndex);
                    var started = Volatile.Read(ref run.CurrentStarted);
                    if (index >= 0 && started != 0 && Stopwatch.GetElapsedTime(started) > perHandleLimit)
                    {
                        // This handle is stuck (or very slow). Leave the thread to it and carry on with the next handle on a
                        // fresh thread; the stuck one is a background thread and never touches the results again.
                        run.Abandoned = true;
                        skipped++;
                        restartAt = index + 1;
                        break;
                    }
                }

                if (restartAt < 0)
                    break;
                start = restartAt;
            }

            return Snapshot(results);
        }
        finally
        {
            foreach (var handle in sourceProcesses.Values)
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }
    }

    private static List<(int ProcessId, string Path)> Snapshot(List<(int ProcessId, string Path)> results)
    {
        lock (results)
            return [.. results];
    }

    private static void NameRange(
        List<HandleEntry> entries,
        int from,
        NamingRun run,
        ConcurrentDictionary<int, IntPtr> sourceProcesses,
        List<(int ProcessId, string Path)> results)
    {
        try
        {
            var self = GetCurrentProcess();
            for (var i = from; i < entries.Count; i++)
            {
                if (run.Abandoned)
                    return;

                Volatile.Write(ref run.CurrentStarted, Stopwatch.GetTimestamp());
                Volatile.Write(ref run.CurrentIndex, i);

                var entry = entries[i];
                var source = sourceProcesses.GetOrAdd(entry.ProcessId, static pid => OpenProcess(ProcessDupHandle, false, pid));
                if (source == IntPtr.Zero)
                    continue;

                if (!DuplicateHandle(source, entry.Handle, self, out var duplicate, 0, false, DuplicateSameAccess))
                    continue;

                try
                {
                    if (GetFileType(duplicate) != FileTypeDisk)
                        continue;

                    var path = WindowsPathNames.TryGetFinalPath(duplicate);
                    if (path is null || run.Abandoned)
                        continue;

                    lock (results)
                        results.Add((entry.ProcessId, path));
                }
                finally
                {
                    CloseHandle(duplicate);
                }
            }
        }
        catch
        {
            // A failure on one thread ends this pass early; the watcher returns what was collected.
        }
        finally
        {
            run.Done.Set();
        }
    }
}
