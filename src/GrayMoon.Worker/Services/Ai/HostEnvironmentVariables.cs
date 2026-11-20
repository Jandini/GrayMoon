using System.Runtime.Versioning;

namespace GrayMoon.Worker.Services.Ai;

/// <summary>
/// Reads an environment variable as the user configured it on this host. The Worker's own process value comes first:
/// it is what a CLI started by the Worker inherits. On Windows a variable that is not in the process is then looked up
/// in the user's and the machine's stored environment, because a Worker running as a service keeps the snapshot the
/// Service Control Manager gave it and does not see a variable set afterwards. Nothing is ever written.
/// </summary>
public static class HostEnvironmentVariables
{
    public static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) && OperatingSystem.IsWindows())
            value = ReadStored(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadStored(string name)
    {
        try
        {
            var user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            return string.IsNullOrEmpty(user)
                ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                : user;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The registry key is not readable for this account: the variable counts as not configured.
            return null;
        }
    }
}
