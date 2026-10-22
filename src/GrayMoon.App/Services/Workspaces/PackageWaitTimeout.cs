namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Package-wait timeout is not user cancel. Report it on the waiting level and stop synchronized push.
/// </summary>
internal static class PackageWaitTimeout
{
    /// <summary>
    /// The wait only looks at the package registry; it does not know which build system publishes a package, so the
    /// message names what is missing and says the publish may still be pending, never a specific CI product.
    /// </summary>
    internal static string FormatMessage(int found, int total, TimeSpan timeout, IReadOnlyList<string>? missing = null)
    {
        var message = $"Timed out waiting for package dependencies after {timeout.TotalMinutes:0.#} min ({found} of {total} found).";
        if (missing is not { Count: > 0 })
            return message;

        return message
            + $" Not yet in the registry: {string.Join(", ", missing)}."
            + " Its build or publish may not have started or finished yet. Run Push Synchronized again to continue.";
    }

    internal static void Report(
        int level,
        Action<int, string>? onLevelError,
        int found,
        int total,
        TimeSpan timeout,
        IReadOnlyList<string>? missing = null)
    {
        onLevelError?.Invoke(level, FormatMessage(found, total, timeout, missing));
    }
}
