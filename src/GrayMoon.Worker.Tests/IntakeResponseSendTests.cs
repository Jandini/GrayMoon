using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Hosted;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A response the Worker cannot deliver used to vanish without a log line, so a request the App reported as failed
/// could not be traced to the dropped answer.
/// </summary>
public sealed class IntakeResponseSendTests
{
    private readonly CapturingLogger<SignalRConnectionHostedService> _logger = new();
    private static readonly WorkerCommandResponse Response = new(false, null, "Command cancelled.");

    [Theory]
    [InlineData(HubConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Disconnected)]
    [InlineData(null)]
    public async Task A_response_produced_while_not_connected_is_not_sent_and_is_logged(HubConnectionState? state)
    {
        var sent = false;

        await SignalRConnectionHostedService.SendIntakeResponseAsync(
            state, () => { sent = true; return Task.CompletedTask; }, "req-17", Response, _logger);

        Assert.False(sent);
        var (level, message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("req-17", message);
        Assert.Contains("Command cancelled.", message);
    }

    [Fact]
    public async Task A_response_is_sent_without_a_warning_while_connected()
    {
        var sent = false;

        await SignalRConnectionHostedService.SendIntakeResponseAsync(
            HubConnectionState.Connected, () => { sent = true; return Task.CompletedTask; }, "req-18", Response, _logger);

        Assert.True(sent);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task A_send_that_throws_is_logged_with_the_request_and_still_reported_to_the_caller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SignalRConnectionHostedService.SendIntakeResponseAsync(
            HubConnectionState.Connected, () => throw new InvalidOperationException("connection went away"), "req-19", Response, _logger));

        var (level, message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("req-19", message);
    }
}