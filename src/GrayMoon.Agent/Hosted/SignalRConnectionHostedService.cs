using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Jobs;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

/// <summary>Custom retry policy that retries every 5 seconds indefinitely.</summary>
internal sealed class FiveSecondRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // First retry immediately, then every 5 seconds
        return retryContext.PreviousRetryCount == 0 
            ? TimeSpan.Zero 
            : TimeSpan.FromSeconds(5);
    }
}

public sealed class SignalRConnectionHostedService(
    IHubConnectionProvider hubProvider,
    IJobQueue jobQueue,
    CommandJobFactory commandJobFactory,
    IOptions<AgentOptions> options,
    ILogger<SignalRConnectionHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly AgentOptions _options = options.Value;
    private HubConnection? _connection;

    private async Task ReportSemVerAsync(CancellationToken cancellationToken)
    {
        if (_connection == null) return;

        var agentSemVer = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        try
        {
            await _connection.InvokeAsync("ReportSemVer", agentSemVer, cancellationToken);
            logger.LogInformation("Reported agent SemVer: {SemVer}", agentSemVer);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report SemVer to hub");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_options.AppHubUrl)
            .WithAutomaticReconnect(new FiveSecondRetryPolicy())
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            })
            .Build();

        _connection.On<string, string, JsonElement?>("RequestCommand", async (requestId, command, args) =>
        {
            logger.LogInformation("RequestCommand {RequestId} received: {Command}", requestId, command);
            logger.LogTrace("RequestCommand {RequestId} request content: {Args}", requestId, args.HasValue ? args.Value.GetRawText() : "null");
            var envelope = commandJobFactory.CreateCommandJob(requestId, command, args);
            await jobQueue.EnqueueAsync(envelope, cancellationToken);
        });

        _connection.Reconnecting += error =>
        {
            logger.LogWarning(error, "Connection lost. Reconnecting to hub at {Url}...", _options.AppHubUrl);
            return Task.CompletedTask;
        };

        _connection.Reconnected += async connectionId =>
        {
            logger.LogInformation("Reconnected to hub at {Url} (ConnectionId: {ConnectionId})", _options.AppHubUrl, connectionId);
            await ReportSemVerAsync(CancellationToken.None);
        };

        _connection.Closed += async error =>
        {
            logger.LogWarning(error, "Connection closed. Will attempt to reconnect in 5 seconds...");
            // Start a background task to reconnect if automatic reconnect didn't work
            _ = Task.Run(async () => await ReconnectLoopAsync(cancellationToken), cancellationToken);
            await Task.CompletedTask;
        };

        ((HubConnectionProvider)hubProvider).Connection = _connection;

        await ConnectWithRetryAsync(cancellationToken);
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_connection == null) break;
                
                var state = _connection.State;
                if (state == HubConnectionState.Connected)
                {
                    logger.LogInformation("Already connected to hub at {Url}", _options.AppHubUrl);
                    await ReportSemVerAsync(cancellationToken);
                    return;
                }

                await _connection.StartAsync(cancellationToken);
                logger.LogInformation("Connected to hub at {Url}", _options.AppHubUrl);
                await ReportSemVerAsync(cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to connect to hub. Retrying in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_connection == null) break;

                var state = _connection.State;
                if (state == HubConnectionState.Connected)
                {
                    logger.LogInformation("Connection restored. Stopping reconnect loop.");
                    return;
                }

                if (state == HubConnectionState.Disconnected)
                {
                    logger.LogInformation("Attempting to reconnect to hub at {Url}...", _options.AppHubUrl);
                    await _connection.StartAsync(cancellationToken);
                    logger.LogInformation("Successfully reconnected to hub at {Url}", _options.AppHubUrl);
                    await ReportSemVerAsync(cancellationToken);
                    return;
                }

                // If we're in Connecting or Reconnecting state, wait a bit
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconnection attempt failed. Will retry in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            await _connection.StopAsync(cancellationToken);
            logger.LogInformation("Disconnected from hub");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
