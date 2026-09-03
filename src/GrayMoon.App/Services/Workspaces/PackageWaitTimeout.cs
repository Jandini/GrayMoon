namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Package-wait timeout is not user cancel. Report it on the waiting level and stop synchronized push.
/// </summary>
internal static class PackageWaitTimeout
{
    internal static string FormatMessage(int found, int total, TimeSpan timeout)
        => $"Timed out waiting for package dependencies after {timeout.TotalMinutes:0.#} min ({found} of {total} found).";

    internal static void Report(
        int level,
        Action<int, string>? onLevelError,
        int found,
        int total,
        TimeSpan timeout)
    {
        onLevelError?.Invoke(level, FormatMessage(found, total, timeout));
    }
}
