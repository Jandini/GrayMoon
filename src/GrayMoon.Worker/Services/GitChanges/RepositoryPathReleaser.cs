using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.GitChanges;

public sealed class RepositoryPathReleaser(
    RepositoryPathGate gate,
    GitRepositoryWatcherManager watcherManager,
    GitStatusRefreshCoordinator refreshCoordinator,
    ILogger<RepositoryPathReleaser> logger) : IRepositoryPathReleaser
{
    private static readonly TimeSpan ScanDrainTimeout = TimeSpan.FromSeconds(10);

    public async Task<IDisposable> ReleaseAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        // Block first so nothing can start a watcher or scan between the dispose below and the caller's delete.
        var scope = gate.Block(paths);
        try
        {
            var roots = gate.Snapshot();
            var released = watcherManager.ReleaseUnder(roots);
            await refreshCoordinator.DrainScansUnderAsync(roots, ScanDrainTimeout, cancellationToken);
            logger.LogDebug("Released {Count} git watcher(s) under {Paths}", released, string.Join("; ", paths));
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
