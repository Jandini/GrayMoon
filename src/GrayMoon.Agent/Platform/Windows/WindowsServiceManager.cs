using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GrayMoon.Agent.Platform.Windows;

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
