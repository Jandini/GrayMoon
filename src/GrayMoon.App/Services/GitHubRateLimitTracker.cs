using System.Collections.Concurrent;

namespace GrayMoon.App.Services;

/// <summary>
/// Tracks the most recently observed GitHub rate-limit snapshot per connector (recorded from every response,
/// not just failures), and a shared "paused until" backoff gate per connector so a 429 hit by any poller
/// (grid, live-feed terminal, push discovery) pauses every other poller for that connector too - regardless
/// of which DI scope resolved them.
/// </summary>
public interface IGitHubRateLimitTracker
{
    void Record(string connectorName, GitHubRateLimitSnapshot snapshot);

    GitHubRateLimitSnapshot? GetLatest(string connectorName);

    void PauseUntil(string connectorName, DateTimeOffset until);

    DateTimeOffset? GetPausedUntil(string connectorName);
}

public sealed class GitHubRateLimitTracker : IGitHubRateLimitTracker
{
    private readonly ConcurrentDictionary<string, GitHubRateLimitSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pausedUntil = new();

    public void Record(string connectorName, GitHubRateLimitSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(connectorName))
            return;

        _snapshots[connectorName] = snapshot;
    }

    public GitHubRateLimitSnapshot? GetLatest(string connectorName)
    {
        return _snapshots.TryGetValue(connectorName, out var snapshot) ? snapshot : null;
    }

    public void PauseUntil(string connectorName, DateTimeOffset until)
    {
        if (string.IsNullOrWhiteSpace(connectorName))
            return;

        _pausedUntil.AddOrUpdate(connectorName, until, (_, existing) => until > existing ? until : existing);
    }

    public DateTimeOffset? GetPausedUntil(string connectorName)
    {
        if (!_pausedUntil.TryGetValue(connectorName, out var until))
            return null;

        if (until <= DateTimeOffset.UtcNow)
        {
            _pausedUntil.TryRemove(connectorName, out _);
            return null;
        }

        return until;
    }
}
