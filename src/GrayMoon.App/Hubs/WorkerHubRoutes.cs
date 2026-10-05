namespace GrayMoon.App.Hubs;

/// <summary>
/// URL paths of the Worker SignalR hub. <see cref="Path"/> is the current path; <see cref="LegacyPath"/> is the
/// path used before the Agent -> Worker rename. Already-installed Workers keep their service arguments
/// (<c>--hub-url .../hub/agent</c>), so the legacy path stays mapped to the same hub and gets exactly the same
/// protection (Worker secret required, no Origin header allowed).
/// </summary>
public static class WorkerHubRoutes
{
    public const string Path = "/hub/worker";

    public const string LegacyPath = "/hub/agent";

    /// <summary>True when <paramref name="path"/> is under the Worker hub, at its current or legacy path.</summary>
    public static bool IsWorkerHubPath(string path) =>
        path.StartsWith(Path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(LegacyPath, StringComparison.OrdinalIgnoreCase);
}
