using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>
/// Thin wrapper over the Windows Restart Manager (rstrtmgr.dll): registers files in a private session and asks which processes
/// use them. Restart Manager only works on files (not folders) and only reports, it never closes anything here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    private enum RmAppType
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>One process reported by Restart Manager.</summary>
    internal sealed record RmProcess(int ProcessId, string? AppName, string? ServiceName, BlockingProcessKind Kind);

    /// <summary>
    /// Registers <paramref name="files"/> in batches of <paramref name="batchSize"/> in one session and returns the processes using
    /// any of them. Returns an error string instead of throwing when Restart Manager itself fails.
    /// </summary>
    internal static (IReadOnlyList<RmProcess> Processes, string? Error) GetProcessesUsingFiles(
        IReadOnlyList<string> files,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return ([], null);

        var key = new char[CchRmSessionKey + 1];
        var startResult = RmStartSession(out var session, 0, key);
        if (startResult != ErrorSuccess)
            return ([], $"Restart Manager could not start a session (error {startResult}).");

        try
        {
            for (var offset = 0; offset < files.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = files.Skip(offset).Take(batchSize).ToArray();
                var registerResult = RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null);
                if (registerResult != ErrorSuccess)
                    return ([], $"Restart Manager could not register the files (error {registerResult}).");
            }

            cancellationToken.ThrowIfCancellationRequested();

            uint rebootReasons = 0;
            uint count = 0;
            RmProcessInfo[]? buffer = null;
            int listResult;
            // The process list can grow between the size query and the read; retry a few times on ERROR_MORE_DATA.
            for (var attempt = 0; ; attempt++)
            {
                listResult = RmGetList(session, out var needed, ref count, buffer, ref rebootReasons);
                if (listResult == ErrorMoreData && attempt < 5)
                {
                    count = needed;
                    buffer = new RmProcessInfo[needed];
                    continue;
                }

                break;
            }

            if (listResult != ErrorSuccess)
                return ([], $"Restart Manager could not list the processes (error {listResult}).");

            if (buffer is null || count == 0)
                return ([], null);

            var processes = new List<RmProcess>((int)count);
            for (var i = 0; i < count; i++)
            {
                var info = buffer[i];
                processes.Add(new RmProcess(
                    info.Process.dwProcessId,
                    string.IsNullOrWhiteSpace(info.strAppName) ? null : info.strAppName,
                    string.IsNullOrWhiteSpace(info.strServiceShortName) ? null : info.strServiceShortName,
                    MapKind(info.ApplicationType)));
            }

            return (processes, null);
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static BlockingProcessKind MapKind(RmAppType type) => type switch
    {
        RmAppType.RmMainWindow or RmAppType.RmOtherWindow => BlockingProcessKind.Application,
        RmAppType.RmService => BlockingProcessKind.Service,
        RmAppType.RmExplorer => BlockingProcessKind.Explorer,
        RmAppType.RmConsole => BlockingProcessKind.Console,
        RmAppType.RmCritical => BlockingProcessKind.Critical,
        _ => BlockingProcessKind.Unknown
    };
}
