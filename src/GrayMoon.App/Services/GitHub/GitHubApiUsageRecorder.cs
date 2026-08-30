using System.Collections.Concurrent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// Records every GitHub API call (including retried attempts) into in-memory hourly counters, then flushes
/// them to <see cref="GitHubApiUsageHourly"/> periodically. In-memory counting keeps the hot path (every
/// GitHub call) free of a DB round-trip; periodic flush keeps counts from being lost entirely on restart
/// during development (see the "restart-surviving persistence" test), at the cost of losing at most one
/// flush interval's worth of counts if the app crashes uncleanly.
/// </summary>
public interface IGitHubApiUsageRecorder
{
    void Record(int connectorId, string requestUri, bool isNotModified, bool isError);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHubApiUsageRecorder(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<GitHubApiUsageRecorder> logger) : IGitHubApiUsageRecorder
{
    private sealed class Counter
    {
        public int RequestCount;
        public int NotModifiedCount;
        public int ErrorCount;
    }

    private readonly ConcurrentDictionary<(int ConnectorId, string Category, DateTime HourBucketUtc), Counter> _counters = new();

    public void Record(int connectorId, string requestUri, bool isNotModified, bool isError)
    {
        var category = GitHubApiUsageCategoryMapper.Categorize(requestUri);
        var hourBucket = TruncateToHour(DateTime.UtcNow);
        var counter = _counters.GetOrAdd((connectorId, category, hourBucket), static _ => new Counter());

        Interlocked.Increment(ref counter.RequestCount);
        if (isNotModified)
            Interlocked.Increment(ref counter.NotModifiedCount);
        if (isError)
            Interlocked.Increment(ref counter.ErrorCount);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_counters.IsEmpty)
            return;

        // Snapshot-and-remove so counts recorded concurrently during the flush start a fresh entry rather
        // than being lost or double-flushed.
        var snapshot = new List<((int ConnectorId, string Category, DateTime HourBucketUtc) Key, Counter Counter)>();
        foreach (var key in _counters.Keys.ToArray())
        {
            if (_counters.TryRemove(key, out var counter))
                snapshot.Add((key, counter));
        }

        if (snapshot.Count == 0)
            return;

        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            foreach (var (key, counter) in snapshot)
            {
                var row = await dbContext.GitHubApiUsageHourly.FirstOrDefaultAsync(
                    u => u.ConnectorId == key.ConnectorId && u.Category == key.Category && u.HourBucketUtc == key.HourBucketUtc,
                    cancellationToken);

                if (row == null)
                {
                    row = new GitHubApiUsageHourly
                    {
                        ConnectorId = key.ConnectorId,
                        Category = key.Category,
                        HourBucketUtc = key.HourBucketUtc
                    };
                    dbContext.GitHubApiUsageHourly.Add(row);
                }

                row.RequestCount += counter.RequestCount;
                row.NotModifiedCount += counter.NotModifiedCount;
                row.ErrorCount += counter.ErrorCount;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to flush GitHub API usage counters; this interval's counts are lost, counting resumes for the next interval");
        }
    }

    private static DateTime TruncateToHour(DateTime utcNow) =>
        new(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
}
