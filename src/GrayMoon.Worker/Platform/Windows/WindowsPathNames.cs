using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>Path name helpers for lock diagnostics: final paths of open handles, and NT device paths turned into drive paths.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPathNames
{
    private const uint FileNameNormalized = 0x0;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);

    /// <summary>The normalized drive-letter (or UNC) path of an open file or folder handle, or null when it has none.</summary>
    internal static string? TryGetFinalPath(IntPtr handle)
    {
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized);
        if (length == 0)
            return null;

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized);
            if (length == 0 || length >= buffer.Length)
                return null;
        }

        return StripExtendedPrefix(new string(buffer, 0, (int)length));
    }

    /// <summary>
    /// The final path of an existing file or folder after junctions, symlinks and drive mappings are resolved, or null. Opens it
    /// with no access rights and full sharing, so it never gets in anyone's way.
    /// </summary>
    internal static string? TryResolveFinalPath(string path)
    {
        try
        {
            using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            return handle.IsInvalid ? null : TryGetFinalPath(handle.DangerousGetHandle());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the <c>\\?\</c> or <c>\\?\UNC\</c> prefix that final path names carry.</summary>
    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    /// <summary>Maps NT device names (<c>\Device\HarddiskVolume3</c>) to their drive letters (<c>C:</c>).</summary>
    internal static IReadOnlyDictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var length = QueryDosDeviceW(drive, buffer, (uint)buffer.Length);
            if (length == 0)
                continue;

            // The result is a list of NUL-terminated strings; the first one is the current mapping.
            var target = new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            // A subst drive maps to "\??\C:\folder"; the real drive is already in the map under its own device.
            if (string.IsNullOrEmpty(target) || target.StartsWith(@"\??\", StringComparison.Ordinal))
                continue;

            map.TryAdd(target, drive);
        }

        return map;
    }

    /// <summary>Turns an NT device path from <c>GetMappedFileName</c> into a drive-letter or UNC path, or null when it cannot.</summary>
    internal static string? DevicePathToDos(string devicePath, IReadOnlyDictionary<string, string> deviceMap)
    {
        const string networkPrefix = @"\Device\Mup\";
        if (devicePath.StartsWith(networkPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + devicePath[networkPrefix.Length..];

        foreach (var (device, drive) in deviceMap)
        {
            if (devicePath.Length > device.Length
                && devicePath[device.Length] == '\\'
                && devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return drive + devicePath[device.Length..];
            }
        }

        return null;
    }
}
