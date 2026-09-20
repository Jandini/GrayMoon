using System.Collections.Concurrent;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Silent +/- fill-in while the Changes page is open. Watcher porcelain scans stay fast; this
/// follows up with numstat in the background and persists when ready so the header can show
/// totals without a manual Refresh. Refresh still runs the same scan with line stats included.
/// </summary>
public interface IGitChangesLineStatsRefresh
{
    /// <summary>Marks this workspace as wanting background +/- while the Changes page is open.</summary>
    IDisposable Subscribe(int workspaceId);

    /// <summary>Fills +/- for every repository in the workspace. No-op when nobody is subscribed.</summary>
    void RequestWorkspace(int workspaceId);

    /// <summary>
    /// Fills +/- for one repository after a porcelain-only snapshot. Debounced so a burst of
    /// watcher events becomes one numstat. No-op when nobody is subscribed.
    /// </summary>
    void RequestRepository(int workspaceId, int repositoryId);
}

public sealed class GitChangesLineStatsRefresh(
    IGitChangesWorkspaceScanner scanner,
    IOptions<GitChangesOptions> options,
    ILogger<GitChangesLineStatsRefresh> logger) : IGitChangesLineStatsRefresh
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _interest = [];
    private readonly HashSet<int> _workspaceInFlight = [];
    private readonly ConcurrentDictionary<RepoKey, CancellationTokenSource> _repoDebounce = new();

    public IDisposable Subscribe(int workspaceId)
    {
        lock (_gate)
        {
            _interest[workspaceId] = _interest.GetValueOrDefault(workspaceId) + 1;
        }

        return new Lease(this, workspaceId);
    }

    public void RequestWorkspace(int workspaceId)
    {
        if (!IsInterested(workspaceId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_workspaceInFlight.Add(workspaceId))
            {
                return;
            }
        }

        CancelRepositoryDebounces(workspaceId);
        _ = RunWorkspaceAsync(workspaceId);
    }

    public void RequestRepository(int workspaceId, int repositoryId)
    {
        if (!IsInterested(workspaceId))
        {
            return;
        }

        lock (_gate)
        {
            if (_workspaceInFlight.Contains(workspaceId))
            {
                return;
            }
        }

        var key = new RepoKey(workspaceId, repositoryId);
        var cts = new CancellationTokenSource();
        if (_repoDebounce.TryRemove(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _repoDebounce[key] = cts;
        _ = RunRepositoryDebouncedAsync(key, cts);
    }

    private bool IsInterested(int workspaceId)
    {
        lock (_gate)
        {
            return _interest.GetValueOrDefault(workspaceId) > 0;
        }
    }

    private async Task RunWorkspaceAsync(int workspaceId)
    {
        try
        {
            await scanner.ScanWorkspaceAsync(workspaceId, CancellationToken.None, includeLineStats: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Background +/- fill failed for workspace {WorkspaceId}", workspaceId);
        }
        finally
        {
            lock (_gate)
            {
                _workspaceInFlight.Remove(workspaceId);
            }
        }
    }

    private async Task RunRepositoryDebouncedAsync(RepoKey key, CancellationTokenSource cts)
    {
        try
        {
            var delay = Math.Max(0, options.Value.WatcherDebounceMilliseconds);
            if (delay > 0)
            {
                await Task.Delay(delay, cts.Token);
            }

            if (!IsInterested(key.WorkspaceId))
            {
                return;
            }

            await scanner.ScanWorkspaceAsync(
                key.WorkspaceId,
                cts.Token,
                includeLineStats: true,
                repositoryId: key.RepositoryId);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later request or the Changes page closed.
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Background +/- fill failed for workspace {WorkspaceId} repo {RepositoryId}",
                key.WorkspaceId,
                key.RepositoryId);
        }
        finally
        {
            if (_repoDebounce.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
            {
                _repoDebounce.TryRemove(key, out _);
            }

            cts.Dispose();
        }
    }

    private void Release(int workspaceId)
    {
        lock (_gate)
        {
            if (!_interest.TryGetValue(workspaceId, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _interest.Remove(workspaceId);
            }
            else
            {
                _interest[workspaceId] = count - 1;
                return;
            }
        }

        CancelRepositoryDebounces(workspaceId);
    }

    private void CancelRepositoryDebounces(int workspaceId)
    {
        foreach (var key in _repoDebounce.Keys)
        {
            if (key.WorkspaceId != workspaceId)
            {
                continue;
            }

            if (_repoDebounce.TryRemove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private readonly record struct RepoKey(int WorkspaceId, int RepositoryId);

    private sealed class Lease(GitChangesLineStatsRefresh owner, int workspaceId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            owner.Release(workspaceId);
        }
    }
}
