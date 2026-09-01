namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// Every 30 minutes, logs in-memory GitHub API usage since the last snapshot per connector (total calls, 304s,
/// errors, broken down by category) alongside the most recently observed rate-limit remaining/reset from
/// <see cref="IGitHubRateLimitTracker"/>. Deliberately does not make its own GitHub call to cross-check
/// (e.g. GET /rate_limit) - that would itself be a periodic background API consumer, which defeats the
/// purpose of this service.
/// </summary>
public sealed class GitHubApiUsageLoggerBackgroundService(
    IGitHubApiUsageRecorder recorder,
    IGitHubRateLimitTracker rateLimitTracker,
    ILogger<GitHubApiUsageLoggerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GitHubApiUsageLoggerBackgroundService starting (summary every {IntervalMinutes} min)", LogInterval.TotalMinutes);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(LogInterval, stoppingToken);
                LogUsage(logWhenEmpty: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down - log whatever is left so the last interval is not silently dropped.
            LogUsage(logWhenEmpty: false);
        }
    }

    private void LogUsage(bool logWhenEmpty)
    {
        try
        {
            var entries = recorder.TakeSnapshot();
            if (entries.Count == 0)
            {
                if (logWhenEmpty)
                    logger.LogInformation("GitHub API usage (last 30 min): no calls recorded");
                return;
            }

            foreach (var group in entries.GroupBy(e => e.ConnectorId))
            {
                var connectorName = group.First().ConnectorName;
                var total = group.Sum(e => e.RequestCount);
                var notModified = group.Sum(e => e.NotModifiedCount);
                var errors = group.Sum(e => e.ErrorCount);
                var byCategory = string.Join(", ", group
                    .OrderByDescending(e => e.RequestCount)
                    .Select(e => $"{e.Category}={e.RequestCount}"));

                var snapshot = rateLimitTracker.GetLatest(connectorName);
                var quotaText = snapshot is { } s
                    ? $", remaining {Format(s.Remaining)}/{Format(s.Limit)}"
                      + (s.ResetEpochUtcSeconds is long epoch
                          ? $", resets {DateTimeOffset.FromUnixTimeSeconds(epoch):yyyy-MM-dd HH:mm:ss} UTC"
                          : string.Empty)
                    : ", no rate-limit snapshot observed yet";

                logger.LogInformation(
                    "GitHub API usage (last 30 min) for {ConnectorName}: {Total} calls ({NotModified} not-modified, {Errors} errors) [{ByCategory}]{Quota}",
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
