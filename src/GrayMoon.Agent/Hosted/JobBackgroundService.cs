using System.Diagnostics;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
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
                {
                    var nsw = Stopwatch.StartNew();
                    await notifySyncHandler.ExecuteAsync(envelope.NotifyJob, stoppingToken);
                    logger.LogInformation("NotifySync completed for repo={RepoId} workspace={WorkspaceId} in {ElapsedMs}ms",
                        envelope.NotifyJob.RepositoryId, envelope.NotifyJob.WorkspaceId, nsw.ElapsedMilliseconds);
                }
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
                    await SendResponseAsync(envelope.CommandJob.RequestId, new AgentCommandResponse(false, null, ex.Message));
                }
            }
        }
    }

    private async Task ProcessCommandAsync(ICommandJob job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await dispatcher.ExecuteAsync(job.Command, job.Request, ct);
        sw.Stop();
        var (success, error) = GetCommandSuccessAndError(result);
        logger.LogInformation("ResponseCommand {RequestId} completed ({Command}) in {ElapsedMs}ms", job.RequestId, job.Command, sw.ElapsedMilliseconds);
        logger.LogTrace("ResponseCommand {RequestId} response content: {@ResponseBody}", job.RequestId, result);
        await SendResponseAsync(job.RequestId, new AgentCommandResponse(success, result, error));
    }

    /// <summary>If result has a Success property that is false, return (false, ErrorMessage); otherwise (true, null).</summary>
    private static (bool success, string? error) GetCommandSuccessAndError(object? result)
    {
        if (result == null) return (true, null);
        var type = result.GetType();
        var successProp = type.GetProperty("Success", typeof(bool));
        if (successProp?.GetValue(result) is true) return (true, null);
        if (successProp?.GetValue(result) is false)
        {
            var errorProp = type.GetProperty("ErrorMessage", typeof(string));
            var msg = errorProp?.GetValue(result) as string;
            return (false, !string.IsNullOrWhiteSpace(msg) ? msg : "Command failed.");
        }
        return (true, null);
    }

    private async Task SendResponseAsync(string requestId, AgentCommandResponse response)
    {
        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
            await connection.InvokeAsync(AgentHubMethods.ResponseCommand, requestId, response);
        else
        {
            logger.LogWarning("Hub not connected, cannot send ResponseCommand for {RequestId}", requestId);
        }
    }
}
