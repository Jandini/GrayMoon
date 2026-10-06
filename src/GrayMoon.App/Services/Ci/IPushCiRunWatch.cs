using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// Streams CI runs triggered by already-pushed repositories into the push overlay while synchronized push
/// waits for packages. One instance per dependency level; it keeps its own discovery and polling state.
/// </summary>
public interface IPushCiRunWatch
{
    /// <summary>
    /// Called on every tick of the package-wait loop. Implementations throttle themselves, so calling this more
    /// often than they want to poll is fine. <paramref name="pushedRepos"/> are the repositories pushed by earlier levels.
    /// </summary>
    Task TickAsync(
        IReadOnlyList<PushRepoPayload> pushedRepos,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        CancellationToken cancellationToken);
}
