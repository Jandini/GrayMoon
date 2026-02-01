using System.Text.Json;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Models;
using GrayMoon.Agent.Queue;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

public sealed class SignalRConnectionHostedService : IHostedService, IAsyncDisposable
{
    private readonly IHubConnectionProvider _hubProvider;
    private readonly IJobQueue _jobQueue;
    private readonly AgentOptions _options;
    private readonly ILogger<SignalRConnectionHostedService> _logger;
    private HubConnection? _connection;

    public SignalRConnectionHostedService(
        IHubConnectionProvider hubProvider,
        IJobQueue jobQueue,
        IOptions<AgentOptions> options,
        ILogger<SignalRConnectionHostedService> logger)
    {
        _hubProvider = hubProvider;
        _jobQueue = jobQueue;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_options.AppHubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string, string, JsonElement?>("RequestCommand", async (requestId, command, args) =>
        {
            _logger.LogInformation("Received RequestCommand: {RequestId}, {Command}", requestId, command);
            var job = new QueuedJob
            {
                RequestId = requestId,
                Command = command,
                Args = args
            };
            await _jobQueue.EnqueueAsync(job, cancellationToken);
        });

        ((HubConnectionProvider)_hubProvider).Connection = _connection;

        await ConnectWithRetryAsync(cancellationToken);
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connection!.StartAsync(cancellationToken);
                _logger.LogInformation("Connected to hub at {Url}", _options.AppHubUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to hub. Retrying in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            await _connection.StopAsync(cancellationToken);
            _logger.LogInformation("Disconnected from hub");
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
