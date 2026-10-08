using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>
/// Files a process has mapped into memory: its executable, DLLs and .NET assemblies, and memory-mapped data files. A program
/// running from a folder (a test host from <c>bin\Debug</c>) keeps those files in use without any open file handle, so the
/// handle table alone misses it. Read-only: walks the address space with <c>VirtualQueryEx</c> and asks
/// <c>GetMappedFileName</c> once per mapped allocation.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMappedFiles
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemImage = 0x1000000;
    private const uint MemMapped = 0x40000;
    private const int DeadlineCheckInterval = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetMappedFileNameW(IntPtr hProcess, IntPtr lpv, [Out] char[] lpFilename, uint nSize);

    /// <summary>
    /// Drive-letter paths of the files <paramref name="processId"/> has mapped, or null when the process cannot be opened.
    /// Stops early (returning what it found) at <paramref name="deadlineUtc"/>.
    /// </summary>
    internal static IReadOnlyCollection<string>? TryGetMappedFilePaths(
        int processId,
        IReadOnlyDictionary<string, string> deviceMap,
        DateTime deadlineUtc)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var name = new char[1024];
            var infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            var address = IntPtr.Zero;
            var lastAllocation = new IntPtr(-1);
            var iterations = 0;

            while (VirtualQueryEx(handle, address, out var info, infoSize) != 0)
            {
                if (info.State == MemCommit
                    && (info.Type == MemImage || info.Type == MemMapped)
                    && info.AllocationBase != lastAllocation)
                {
                    lastAllocation = info.AllocationBase;
                    var length = GetMappedFileNameW(handle, info.BaseAddress, name, (uint)name.Length);
                    if (length > 0 && length < name.Length)
                    {
                        var dosPath = WindowsPathNames.DevicePathToDos(new string(name, 0, (int)length), deviceMap);
                        if (dosPath is not null)
                            paths.Add(dosPath);
                    }
                }

                var next = (long)info.BaseAddress + (long)info.RegionSize;
                if (next <= (long)address)
                    break;
                address = new IntPtr(next);

                if (++iterations % DeadlineCheckInterval == 0 && DateTime.UtcNow >= deadlineUtc)
                    break;
            }

            return paths;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
