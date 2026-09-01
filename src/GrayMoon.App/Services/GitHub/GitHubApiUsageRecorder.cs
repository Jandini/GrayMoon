using System.Collections.Concurrent;

namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// One interval's counts for a single connector + category, produced by
/// <see cref="IGitHubApiUsageRecorder.TakeSnapshot"/>.
/// </summary>
public sealed record GitHubApiUsageSnapshotEntry(
    int ConnectorId,
    string ConnectorName,
    string Category,
    int RequestCount,
    int NotModifiedCount,
    int ErrorCount);

/// <summary>
/// Records every GitHub API call (including retried attempts) into in-memory counters. The logger hosted
/// service snapshots these every 30 minutes. Counts are process-lifetime only - a restart starts from zero.
/// </summary>
public interface IGitHubApiUsageRecorder
{
    void Record(int connectorId, string connectorName, string requestUri, bool isNotModified, bool isError);

    /// <summary>
    /// Snapshot-and-remove so counts recorded concurrently during logging start a fresh entry rather than
    /// being lost or double-logged.
    /// </summary>
    IReadOnlyList<GitHubApiUsageSnapshotEntry> TakeSnapshot();
}

public sealed class GitHubApiUsageRecorder : IGitHubApiUsageRecorder
{
    private sealed class Counter
    {
        public string ConnectorName = string.Empty;
        public int RequestCount;
        public int NotModifiedCount;
        public int ErrorCount;
    }

    private readonly ConcurrentDictionary<(int ConnectorId, string Category), Counter> _counters = new();

    public void Record(int connectorId, string connectorName, string requestUri, bool isNotModified, bool isError)
    {
        var category = GitHubApiUsageCategoryMapper.Categorize(requestUri);
        var counter = _counters.GetOrAdd((connectorId, category), static _ => new Counter());

        if (!string.IsNullOrEmpty(connectorName))
            counter.ConnectorName = connectorName;

        Interlocked.Increment(ref counter.RequestCount);
        if (isNotModified)
            Interlocked.Increment(ref counter.NotModifiedCount);
        if (isError)
            Interlocked.Increment(ref counter.ErrorCount);
    }

    public IReadOnlyList<GitHubApiUsageSnapshotEntry> TakeSnapshot()
    {
        if (_counters.IsEmpty)
            return [];

        var snapshot = new List<GitHubApiUsageSnapshotEntry>();
        foreach (var key in _counters.Keys.ToArray())
        {
            if (!_counters.TryRemove(key, out var counter))
                continue;

            var name = string.IsNullOrEmpty(counter.ConnectorName)
                ? $"connector#{key.ConnectorId}"
                : counter.ConnectorName;

            snapshot.Add(new GitHubApiUsageSnapshotEntry(
                key.ConnectorId,
                name,
                key.Category,
                counter.RequestCount,
                counter.NotModifiedCount,
                counter.ErrorCount));
        }

        return snapshot;
    }
}
