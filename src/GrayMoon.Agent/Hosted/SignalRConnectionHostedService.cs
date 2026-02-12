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

public sealed class SignalRConnectionHostedService(
    IHubConnectionProvider hubProvider,
    IJobQueue jobQueue,
    CommandJobFactory commandJobFactory,
    IOptions<AgentOptions> options,
    ILogger<SignalRConnectionHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly AgentOptions _options = options.Value;
    private HubConnection? _connection;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_options.AppHubUrl)
            .WithAutomaticReconnect()
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

        ((HubConnectionProvider)hubProvider).Connection = _connection;

        await ConnectWithRetryAsync(cancellationToken);
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connection!.StartAsync(cancellationToken);
                logger.LogInformation("Connected to hub at {Url}", _options.AppHubUrl);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to connect to hub. Retrying in 5s...");
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
