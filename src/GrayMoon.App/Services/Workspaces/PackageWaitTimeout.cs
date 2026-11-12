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
    /// <param name="readOnlyProducers">
    /// Read-only repositories of this Workspace that produce a missing package. GrayMoon does not push them, so waiting
    /// longer does not help: the message says so and names them.
    /// </param>
    internal static string FormatMessage(
        int found,
        int total,
        TimeSpan timeout,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? readOnlyProducers = null)
    {
        var message = $"Timed out waiting for package dependencies after {timeout.TotalMinutes:0.#} min ({found} of {total} found).";
        if (missing is not { Count: > 0 })
            return message;

        message += $" Not yet in the registry: {string.Join(", ", missing)}.";
        if (readOnlyProducers is { Count: > 0 })
        {
            return message
                + (readOnlyProducers.Count == 1
                    ? $" Repository {readOnlyProducers[0]} produces a missing package and is read-only"
                    : $" Repositories {string.Join(", ", readOnlyProducers)} produce missing packages and are read-only")
                + $": GrayMoon does not push {(readOnlyProducers.Count == 1 ? "it" : "them")}."
                + " Publish the package from outside GrayMoon, or use a version that is already published.";
        }

        return message
            + " Its build or publish may not have started or finished yet. Run Push Synchronized again to continue.";
    }

    internal static void Report(
        int level,
        Action<int, string>? onLevelError,
        int found,
        int total,
        TimeSpan timeout,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? readOnlyProducers = null)
    {
        onLevelError?.Invoke(level, FormatMessage(found, total, timeout, missing, readOnlyProducers));
    }
}
