using GrayMoon.App.Services.Ui;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>
/// A page whose first hub start failed stayed without live updates until it was reloaded: automatic reconnect only
/// covers a connection that was up once. The start is now retried a bounded number of times and never throws.
/// </summary>
public sealed class WorkspaceSyncHubStartTests
{
    private readonly ManualClock _clock = new();
    private readonly CapturingLogger<WorkspaceSyncHubStartTests> _logger = new();

    private Task<bool> StartAsync(Func<CancellationToken, Task> start, CancellationToken cancellationToken = default) =>
        WorkspaceSyncHubConnectionHelper.TryStartAsync(start, _logger, _clock, cancellationToken);

    /// <summary>Advances over every pending back-off until the start ends. No real time passes.</summary>
    private async Task<bool> RunToEndAsync(Task<bool> run)
    {
        for (var i = 0; i < 100_000 && !run.IsCompleted; i++)
        {
            if (_clock.NextTimerDelay is { } delay)
                _clock.Advance(delay);
            else
                await Task.Yield();
        }

        Assert.True(run.IsCompleted, "the hub start never ended");
        return await run;
    }

    private string Outcome() => Assert.Single(_logger.Entries, e => e.Message.Contains("hub start for")).Message;

    [Fact]
    public async Task A_start_that_succeeds_is_not_retried()
    {
        var attempts = 0;

        Assert.True(await RunToEndAsync(StartAsync(_ => { attempts++; return Task.CompletedTask; })));

        Assert.Equal(1, attempts);
        Assert.Contains("started after 1 attempt", Outcome());
    }

    [Fact]
    public async Task A_failed_first_start_is_retried_until_it_connects()
    {
        var attempts = 0;

        var connected = await RunToEndAsync(StartAsync(_ =>
            ++attempts < 3 ? Task.FromException(new HttpRequestException("connection refused")) : Task.CompletedTask));

        Assert.True(connected);
        Assert.Equal(3, attempts);
        Assert.Contains("started after 3 attempt", Outcome());
    }

    [Fact]
    public async Task A_start_that_keeps_failing_gives_up_after_the_bounded_retries_without_throwing()
    {
        var attempts = 0;

        var connected = await RunToEndAsync(StartAsync(_ => { attempts++; return Task.FromException(new HttpRequestException("connection refused")); }));

        Assert.False(connected);
        Assert.Equal(WorkspaceSyncHubConnectionHelper.StartRetryDelays.Length + 1, attempts);
        Assert.Contains("failed after 4 attempt", Outcome());
    }

    [Fact]
    public async Task A_connection_disposed_by_its_page_ends_the_start_without_a_retry_or_an_exception()
    {
        var attempts = 0;

        var connected = await RunToEndAsync(StartAsync(_ => { attempts++; return Task.FromException(new ObjectDisposedException("HubConnection")); }));

        Assert.False(connected);
        Assert.Equal(1, attempts);
        Assert.Contains("cancelled by dispose", Outcome());
    }

    [Fact]
    public async Task A_token_cancelled_during_the_back_off_ends_the_start_without_throwing()
    {
        using var disposed = new CancellationTokenSource();
        var attempts = 0;
        var run = StartAsync(_ => { attempts++; return Task.FromException(new HttpRequestException("connection refused")); }, disposed.Token);

        disposed.Cancel();

        Assert.False(await RunToEndAsync(run));
        Assert.Equal(1, attempts);
        Assert.Contains("cancelled by dispose", Outcome());
        Assert.DoesNotContain(_logger.Entries, e => e.Level > LogLevel.Debug);
    }
}