using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace GrayMoon.Agent.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsLsaPolicy
{
    private const uint PolicyAllAccess = 0x000F0FFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaOpenPolicy(
        IntPtr SystemName,
        ref LsaObjectAttributes ObjectAttributes,
        uint DesiredAccess,
        out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaAddAccountRights(
        IntPtr PolicyHandle,
        IntPtr AccountSid,
        LsaUnicodeString[] UserRights,
        uint CountOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr ObjectHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint Status);

    public static void GrantServiceLogonRight(SecurityIdentifier sid)
    {
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var attributes = new LsaObjectAttributes
        {
            Length = (uint)Marshal.SizeOf<LsaObjectAttributes>()
        };

        var sidHandle = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        var policyHandle = IntPtr.Zero;
        var rightPtr = IntPtr.Zero;

        try
        {
            var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyAllAccess, out policyHandle);
            if (status != 0)
                throw new Win32Exception(LsaNtStatusToWinError(status), "LsaOpenPolicy failed.");

            const string right = "SeServiceLogonRight";
            rightPtr = Marshal.StringToHGlobalUni(right);

            var rights = new LsaUnicodeString[1];
            rights[0].Buffer = rightPtr;
            rights[0].Length = (ushort)(right.Length * 2);
            rights[0].MaximumLength = (ushort)((right.Length + 1) * 2);

            status = LsaAddAccountRights(policyHandle, sidHandle.AddrOfPinnedObject(), rights, 1);
            if (status != 0)
                throw new Win32Exception(LsaNtStatusToWinError(status), "LsaAddAccountRights failed.");
        }
        finally
        {
            if (rightPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(rightPtr);
            if (policyHandle != IntPtr.Zero)
                LsaClose(policyHandle);
            if (sidHandle.IsAllocated)
                sidHandle.Free();
        }
    }
}
