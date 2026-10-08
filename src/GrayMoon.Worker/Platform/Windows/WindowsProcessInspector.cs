using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>
/// Read-only per-process queries used by lock diagnostics: the executable path and the current directory. A process that cannot
/// be opened (another user, elevated, protected) simply yields null; nothing here ever writes to or signals a process.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessInspector
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessWow64InformationClass = 26;

    // x64 layout: PEB.ProcessParameters at 0x20, RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath at 0x38.
    private const int Peb64ProcessParametersOffset = 0x20;
    private const int Params64CurrentDirectoryOffset = 0x38;

    // x86 (WOW64) layout: PEB32.ProcessParameters at 0x10, RTL_USER_PROCESS_PARAMETERS32.CurrentDirectory.DosPath at 0x24.
    private const int Peb32ProcessParametersOffset = 0x10;
    private const int Params32CurrentDirectoryOffset = 0x24;

    private const int MaxPathChars = 32767;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, out IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int dwProcessId, out int pSessionId);

    private const uint ProcessTerminate = 0x0001;

    /// <summary>When <paramref name="processId"/> started (UTC), or null when it cannot be read. With the id it identifies the process.</summary>
    internal static DateTime? TryGetStartTimeUtc(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
                ? DateTime.FromFileTimeUtc(creation)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Terminal Services session of <paramref name="processId"/> (0 for services), or null when it cannot be read.</summary>
    internal static int? TryGetSessionId(int processId) =>
        ProcessIdToSessionId(processId, out var session) ? session : null;

    /// <summary>True when this process may end <paramref name="processId"/> (opens and closes a terminate handle; never ends it).</summary>
    internal static bool CanOpenForTerminate(int processId)
    {
        var handle = OpenProcess(ProcessTerminate, false, processId);
        if (handle == IntPtr.Zero)
            return false;

        CloseHandle(handle);
        return true;
    }

    /// <summary>Full executable path of <paramref name="processId"/>, or null when it cannot be read.</summary>
    internal static string? TryGetExecutablePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var size = 1024;
            var builder = new StringBuilder(size);
            return QueryFullProcessImageNameW(handle, 0, builder, ref size) ? builder.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The current directory of <paramref name="processId"/>, read from its process parameters, or null when it cannot be read
    /// (no access, a process that is exiting, or a 64-bit process seen from a 32-bit Worker).
    /// </summary>
    internal static string? TryGetCurrentDirectory(int processId)
    {
        if (!Environment.Is64BitProcess)
            return null;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            // A 32-bit process on 64-bit Windows keeps its live current directory in its 32-bit PEB.
            if (NtQueryInformationProcess(handle, ProcessWow64InformationClass, out IntPtr peb32, IntPtr.Size, out _) == 0
                && peb32 != IntPtr.Zero)
            {
                return ReadCurrentDirectory32(handle, peb32);
            }

            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, ProcessBasicInformationClass, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0
                || info.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            return ReadCurrentDirectory64(handle, info.PebBaseAddress);
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

    private static string? ReadCurrentDirectory64(IntPtr handle, IntPtr peb)
    {
        var parametersAddress = ReadPointer64(handle, peb + Peb64ProcessParametersOffset);
        if (parametersAddress == IntPtr.Zero)
            return null;

        // UNICODE_STRING (x64): USHORT Length, USHORT MaximumLength, 4 bytes padding, PWSTR Buffer.
        var unicodeString = ReadBytes(handle, parametersAddress + Params64CurrentDirectoryOffset, 16);
        if (unicodeString is null)
            return null;

        var length = BitConverter.ToUInt16(unicodeString, 0);
        var buffer = new IntPtr(BitConverter.ToInt64(unicodeString, 8));
        return ReadUnicode(handle, buffer, length);
    }

    private static string? ReadCurrentDirectory32(IntPtr handle, IntPtr peb32)
    {
        var pointerBytes = ReadBytes(handle, peb32 + Peb32ProcessParametersOffset, 4);
        if (pointerBytes is null)
            return null;

        var parametersAddress = new IntPtr(BitConverter.ToUInt32(pointerBytes, 0));
        if (parametersAddress == IntPtr.Zero)
            return null;

        // UNICODE_STRING32: USHORT Length, USHORT MaximumLength, ULONG Buffer.
        var unicodeString = ReadBytes(handle, parametersAddress + Params32CurrentDirectoryOffset, 8);
        if (unicodeString is null)
            return null;

        var length = BitConverter.ToUInt16(unicodeString, 0);
        var buffer = new IntPtr(BitConverter.ToUInt32(unicodeString, 4));
        return ReadUnicode(handle, buffer, length);
    }

    private static IntPtr ReadPointer64(IntPtr handle, IntPtr address)
    {
        var bytes = ReadBytes(handle, address, 8);
        return bytes is null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(bytes, 0));
    }

    private static string? ReadUnicode(IntPtr handle, IntPtr buffer, int byteLength)
    {
        if (buffer == IntPtr.Zero || byteLength <= 0 || byteLength > MaxPathChars * 2)
            return null;

        var bytes = ReadBytes(handle, buffer, byteLength);
        return bytes is null ? null : Encoding.Unicode.GetString(bytes);
    }

    private static byte[]? ReadBytes(IntPtr handle, IntPtr address, int count)
    {
        var buffer = new byte[count];
        return ReadProcessMemory(handle, address, buffer, new IntPtr(count), out var read) && read.ToInt64() == count
            ? buffer
            : null;
    }
}
