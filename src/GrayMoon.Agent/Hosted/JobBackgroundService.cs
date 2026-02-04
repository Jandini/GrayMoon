using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Jobs;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

public sealed class JobBackgroundService : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly ICommandDispatcher _dispatcher;
    private readonly INotifySyncHandler _notifySyncHandler;
    private readonly IHubConnectionProvider _hubProvider;
    private readonly ILogger<JobBackgroundService> _logger;
    private readonly int _maxConcurrent;

    public JobBackgroundService(
        IJobQueue jobQueue,
        ICommandDispatcher dispatcher,
        INotifySyncHandler notifySyncHandler,
        IHubConnectionProvider hubProvider,
        IOptions<AgentOptions> options,
        ILogger<JobBackgroundService> logger)
    {
        _jobQueue = jobQueue;
        _dispatcher = dispatcher;
        _notifySyncHandler = notifySyncHandler;
        _hubProvider = hubProvider;
        _logger = logger;
        _maxConcurrent = Math.Max(1, options.Value.MaxConcurrentCommands);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobBackgroundService starting with {MaxConcurrent} workers", _maxConcurrent);
        var workers = Enumerable.Range(0, _maxConcurrent)
            .Select(i => ProcessAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task ProcessAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var envelope in _jobQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (envelope.Kind == JobKind.Notify && envelope.NotifyJob != null)
                    await _notifySyncHandler.ExecuteAsync(envelope.NotifyJob, stoppingToken);
                else if (envelope.Kind == JobKind.Command && envelope.CommandJob != null)
                    await ProcessCommandAsync(envelope.CommandJob, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} failed processing job", workerId);
                if (envelope.CommandJob != null)
                    await SendResponseAsync(envelope.CommandJob.RequestId, false, null, ex.Message);
            }
        }
    }

    private async Task ProcessCommandAsync(ICommandJob job, CancellationToken ct)
    {
        var result = await _dispatcher.ExecuteAsync(job.Command, job.Request, ct);
        await SendResponseAsync(job.RequestId, true, result, null);
    }

    private async Task SendResponseAsync(string requestId, bool success, object? data, string? error)
    {
        var connection = _hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
            await connection.InvokeAsync("ResponseCommand", requestId, success, data, error);
        else
        {
            _logger.LogWarning("Hub not connected, cannot send ResponseCommand for {RequestId}", requestId);
        }
    }
}
