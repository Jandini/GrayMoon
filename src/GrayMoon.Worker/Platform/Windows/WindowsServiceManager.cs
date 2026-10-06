using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GrayMoon.Worker.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0x000F01FF;
    private const uint ServiceWin32OwnProcess = 0x0010;
    private const uint ServiceAutoStart = 0x0002;
    private const uint ServiceErrorNormal = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceConfigDescription = 1;
    private const uint DeleteAccess = 0x00010000;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref ServiceDescriptionW lpInfo);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "DeleteService")]
    private static extern bool NativeDeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescriptionW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDescription;
    }

    /// <summary>
    /// Native QUERY_SERVICE_CONFIGW. String fields are pointers into the buffer returned by QueryServiceConfig.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    public static void CreateService(string name, string displayName, string binPath, string account, string? password)
    {
        var scm = OpenSCManagerW(null, null, ScManagerCreateService);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = CreateServiceW(
                scm, name, displayName,
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                binPath,
                null, IntPtr.Zero, null,
                account, password);

            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateService failed.");

            CloseServiceHandle(svc);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void UpdateServiceBinPath(string name, string newBinPath)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenServiceW(scm, name, ServiceChangeConfig);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService '{name}' failed.");

            try
            {
                if (!ChangeServiceConfigW(svc,
                    ServiceNoChange, ServiceNoChange, ServiceNoChange,
                    newBinPath, null, IntPtr.Zero, null, null, null, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig failed.");
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void SetServiceDescription(string name, string description)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenServiceW(scm, name, ServiceChangeConfig);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService '{name}' failed.");

            try
            {
                var desc = new ServiceDescriptionW { lpDescription = description };
                if (!ChangeServiceConfig2W(svc, ServiceConfigDescription, ref desc))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig2 failed.");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Account the service is configured to log on as, for example <c>.\User</c> or <c>NT AUTHORITY\LocalSystem</c>.</summary>
    public static string QueryServiceStartName(string name)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryConfig);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService '{name}' failed.");

            try
            {
                QueryServiceConfigW(svc, IntPtr.Zero, 0, out var needed);
                if (needed == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceConfig failed.");

                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!QueryServiceConfigW(svc, buffer, needed, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceConfig failed.");

                    var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
                    var startName = Marshal.PtrToStringUni(config.ServiceStartName);
                    if (string.IsNullOrWhiteSpace(startName))
                        throw new Win32Exception("Service start name was empty.");
                    return startName;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Stores a new logon password for an existing service account. The account name is unchanged.</summary>
    public static void UpdateServicePassword(string name, string account, string password)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenServiceW(scm, name, ServiceChangeConfig);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService '{name}' failed.");

            try
            {
                if (!ChangeServiceConfigW(svc,
                    ServiceNoChange, ServiceNoChange, ServiceNoChange,
                    null, null, IntPtr.Zero, null,
                    account, password, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig failed.");
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void RemoveService(string name)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenServiceW(scm, name, DeleteAccess);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService '{name}' failed.");

            try
            {
                if (!NativeDeleteService(svc))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DeleteService failed.");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
