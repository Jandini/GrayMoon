using System.ComponentModel;

namespace GrayMoon.Worker.Platform.Windows;

/// <summary>
/// Windows returns 1069 (ERROR_SERVICE_LOGON_FAILED) when a service cannot log on,
/// including after the account password stored at install time has changed.
/// </summary>
internal static class ServiceLogonFailure
{
    public const int ErrorServiceLogonFailed = 1069;

    public static bool IsMatch(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32 && win32.NativeErrorCode == ErrorServiceLogonFailed)
                return true;
        }

        return false;
    }
}
