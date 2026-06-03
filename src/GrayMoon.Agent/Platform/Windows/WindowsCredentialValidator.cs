using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GrayMoon.Agent.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsCredentialValidator
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUserW(
        string lpszUsername,
        string lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool Validate(string username, string domain, string password)
    {
        var ok = LogonUserW(username, domain, password,
            Logon32LogonInteractive, Logon32ProviderDefault,
            out var token);

        if (ok && token != IntPtr.Zero)
            CloseHandle(token);

        return ok;
    }
}
