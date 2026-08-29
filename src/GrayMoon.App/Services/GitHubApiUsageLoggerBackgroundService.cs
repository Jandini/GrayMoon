using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>
/// Every 10 minutes, logs the last hour's persisted GitHub API usage per connector (total calls, 304s,
/// errors, broken down by category) alongside the most recently observed rate-limit remaining/reset from
/// <see cref="IGitHubRateLimitTracker"/>. Deliberately does not make its own GitHub call to cross-check
/// (e.g. GET /rate_limit) - that would itself be a periodic background API consumer, which defeats the
/// purpose of this service.
/// </summary>
public sealed class GitHubApiUsageLoggerBackgroundService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IGitHubRateLimitTracker rateLimitTracker,
    ILogger<GitHubApiUsageLoggerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GitHubApiUsageLoggerBackgroundService starting (summary every {IntervalMinutes} min)", LogInterval.TotalMinutes);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(LogInterval, stoppingToken);
                await LogLastHourUsageAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task LogLastHourUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var since = DateTime.UtcNow.AddHours(-1);

            var rows = await dbContext.GitHubApiUsageHourly
                .Where(u => u.HourBucketUtc >= since)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                logger.LogInformation("GitHub API usage (last hour): no calls recorded");
                return;
            }

            var connectorIds = rows.Select(r => r.ConnectorId).Distinct().ToList();
            var connectorNames = await dbContext.Connectors
                .Where(c => connectorIds.Contains(c.ConnectorId))
                .ToDictionaryAsync(c => c.ConnectorId, c => c.ConnectorName, cancellationToken);

            foreach (var group in rows.GroupBy(r => r.ConnectorId))
            {
                var connectorName = connectorNames.TryGetValue(group.Key, out var name) ? name : $"connector#{group.Key}";
                var total = group.Sum(r => r.RequestCount);
                var notModified = group.Sum(r => r.NotModifiedCount);
                var errors = group.Sum(r => r.ErrorCount);
                var byCategory = string.Join(", ", group
                    .GroupBy(r => r.Category)
                    .OrderByDescending(g => g.Sum(r => r.RequestCount))
                    .Select(g => $"{g.Key}={g.Sum(r => r.RequestCount)}"));

                var snapshot = rateLimitTracker.GetLatest(connectorName);
                var quotaText = snapshot is { } s
                    ? $", remaining {Format(s.Remaining)}/{Format(s.Limit)}"
                      + (s.ResetEpochUtcSeconds is long epoch
                          ? $", resets {DateTimeOffset.FromUnixTimeSeconds(epoch):yyyy-MM-dd HH:mm:ss} UTC"
                          : string.Empty)
                    : ", no rate-limit snapshot observed yet";

                logger.LogInformation(
                    "GitHub API usage (last hour) for {ConnectorName}: {Total} calls ({NotModified} not-modified, {Errors} errors) [{ByCategory}]{Quota}",
                    connectorName, total, notModified, errors, byCategory, quotaText);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log GitHub API usage summary");
        }
    }

    private static string Format(int? value) => value?.ToString() ?? "?";
}
