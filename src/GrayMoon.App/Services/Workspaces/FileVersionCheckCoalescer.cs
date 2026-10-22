namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Process-wide coalescer for file-version checks only. Kept separate from the hook-sync
/// <see cref="SyncRecomputeCoalescer"/> singleton so a recompute that itself runs a check cannot nest on the same key.
/// </summary>
public sealed class FileVersionCheckCoalescer
{
    private readonly SyncRecomputeCoalescer _inner = new();

    public Task RunAsync(string key, Func<Task> work) => _inner.RunAsync(key, work);
}
