namespace GrayMoon.App.Services;

/// <summary>Periodically flushes <see cref="IGitHubApiUsageRecorder"/>'s in-memory counters to SQLite.</summary>
public sealed class GitHubApiUsageFlushBackgroundService(
    IGitHubApiUsageRecorder recorder,
    ILogger<GitHubApiUsageFlushBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GitHubApiUsageFlushBackgroundService starting (flush every {IntervalSeconds}s)", FlushInterval.TotalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(FlushInterval, stoppingToken);
                await recorder.FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down - flush whatever is left one last time on a best-effort basis.
            await recorder.FlushAsync(CancellationToken.None);
        }
    }
}
