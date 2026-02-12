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

public sealed class JobBackgroundService(
    IJobQueue jobQueue,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    IOptions<AgentOptions> options,
    ILogger<JobBackgroundService> logger) : BackgroundService
{
    private readonly int _maxConcurrent = Math.Max(1, options.Value.MaxConcurrentCommands);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobBackgroundService starting with {MaxConcurrent} workers", _maxConcurrent);
        var workers = Enumerable.Range(0, _maxConcurrent)
            .Select(i => ProcessAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task ProcessAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var envelope in jobQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (envelope.Kind == JobKind.Notify && envelope.NotifyJob != null)
                    await notifySyncHandler.ExecuteAsync(envelope.NotifyJob, stoppingToken);
                else if (envelope.Kind == JobKind.Command && envelope.CommandJob != null)
                    await ProcessCommandAsync(envelope.CommandJob, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {WorkerId} failed processing job", workerId);
                if (envelope.CommandJob != null)
                {
                    var errorBody = new { Success = false, Error = ex.Message };
                    logger.LogInformation("ResponseCommand {RequestId} failed: {Error}", envelope.CommandJob.RequestId, ex.Message);
                    logger.LogTrace("ResponseCommand {RequestId} response content: {@ResponseBody}", envelope.CommandJob.RequestId, errorBody);
                    await SendResponseAsync(envelope.CommandJob.RequestId, false, null, ex.Message);
                }
            }
        }
    }

    private async Task ProcessCommandAsync(ICommandJob job, CancellationToken ct)
    {
        var result = await dispatcher.ExecuteAsync(job.Command, job.Request, ct);
        logger.LogInformation("ResponseCommand {RequestId} completed ({Command})", job.RequestId, job.Command);
        logger.LogTrace("ResponseCommand {RequestId} response content: {@ResponseBody}", job.RequestId, result);
        await SendResponseAsync(job.RequestId, true, result, null);
    }

    private async Task SendResponseAsync(string requestId, bool success, object? data, string? error)
    {
        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
            await connection.InvokeAsync("ResponseCommand", requestId, success, data, error);
        else
        {
            logger.LogWarning("Hub not connected, cannot send ResponseCommand for {RequestId}", requestId);
        }
    }
}
