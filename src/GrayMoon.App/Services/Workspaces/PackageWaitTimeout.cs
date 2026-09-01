namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Package-wait timeout is not user cancel. Report it on each waiting repo and stop synchronized push.
/// </summary>
internal static class PackageWaitTimeout
{
    internal static string FormatMessage(int found, int total, TimeSpan timeout)
        => $"Timed out waiting for package dependencies after {timeout.TotalMinutes:0.#} min ({found} of {total} found).";

    internal static void Report(
        IEnumerable<int> repoIds,
        Action<int, string>? onRepoError,
        int found,
        int total,
        TimeSpan timeout)
    {
        var message = FormatMessage(found, total, timeout);
        foreach (var repoId in repoIds)
            onRepoError?.Invoke(repoId, message);
    }
}
