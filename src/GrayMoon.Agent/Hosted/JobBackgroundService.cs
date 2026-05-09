using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using GrayMoon.Common;
using Microsoft.AspNetCore.SignalR.Client;
using Polly;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

public sealed class JobBackgroundService(
    IJobQueue jobQueue,
    IAgentQueueTracker queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    IOptions<AgentOptions> options,
    ILogger<JobBackgroundService> logger) : BackgroundService
{
    private readonly int _maxConcurrent = Math.Max(1, options.Value.MaxConcurrentCommands);
    private const int MaxCommandStreamLineLength = 4096;
    private readonly ResiliencePipeline _responseSendPipeline = ResponseCommandSendPipeline.Create(logger);

    private readonly record struct PendingStreamLine(AgentCommandStreamKind Kind, string Text);

    /// <summary>Matches hub JSON style enough for a useful byte-size estimate of the <see cref="AgentCommandResponse"/> argument.</summary>
    private static readonly JsonSerializerOptions ResponsePayloadSizeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

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
                logger.LogError(ex, "Worker {WorkerId} failed processing job. Kind={JobKind}, RequestId={RequestId}, Command={Command}",
                    workerId, envelope.Kind, envelope.CommandJob?.RequestId, envelope.CommandJob?.Command);
                if (envelope.CommandJob != null)
                {
                    var errorBody = new { Success = false, Error = ex.Message };
                    logger.LogInformation("ResponseCommand {RequestId} failed: {Error}", envelope.CommandJob.RequestId, ex.Message);
                    logger.LogTrace("ResponseCommand {RequestId} response content: {@ResponseBody}", envelope.CommandJob.RequestId, errorBody);
                    try
                    {
                        await SendResponseAsync(envelope.CommandJob.RequestId, envelope.CommandJob.Command, new AgentCommandResponse(false, null, ex.Message), stoppingToken);
                    }
                    catch (Exception sendEx)
                    {
                        logger.LogError(sendEx, "Failed sending error ResponseCommand. RequestId={RequestId}, Command={Command}",
                            envelope.CommandJob.RequestId, envelope.CommandJob.Command);
                    }
                }
            }
            finally
            {
                queueTracker.ReportJobCompleted(envelope);
            }
        }
    }

    private async Task ProcessCommandAsync(ICommandJob job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        object? result;
        var connection = hubProvider.Connection;
        if (connection is { State: HubConnectionState.Connected })
        {
            var streamLabel = CommandJobStreamLabel.Resolve(job.Command, job.Request);
            var channel = Channel.CreateUnbounded<PendingStreamLine>();
            var writer = channel.Writer;
            var sendTask = SendCommandOutputLoopAsync(connection, job.RequestId, streamLabel, channel.Reader, ct);
            try
            {
                using (new CommandLineStreamScope(e =>
                {
                    var text = TruncateStreamText(e.Text);
                    writer.TryWrite(new PendingStreamLine(e.Kind, text));
                }))
                {
                    result = await dispatcher.ExecuteAsync(job.Command, job.Request, ct);
                }
            }
            finally
            {
                writer.Complete();
                try
                {
                    await sendTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // shutdown / cancel
                }
            }
        }
        else
        {
            result = await dispatcher.ExecuteAsync(job.Command, job.Request, ct);
        }

        sw.Stop();
        var (success, error) = GetCommandSuccessAndError(result);
        logger.LogInformation("ResponseCommand {RequestId} completed ({Command}) in {ElapsedMs}ms", job.RequestId, job.Command, sw.ElapsedMilliseconds);
        logger.LogTrace("ResponseCommand {RequestId} response content: {@ResponseBody}", job.RequestId, result);
        await SendResponseAsync(job.RequestId, job.Command, new AgentCommandResponse(success, result, error), ct);
    }

    private async Task SendCommandOutputLoopAsync(
        HubConnection connection,
        string requestId,
        string? streamLabel,
        ChannelReader<PendingStreamLine> reader,
        CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (connection.State != HubConnectionState.Connected)
                        break;

                    await connection
                        .InvokeAsync(AgentHubMethods.CommandOutput, requestId, streamLabel, (int)item.Kind, item.Text, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "CommandOutput send failed for {RequestId}", requestId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal on cancel
        }
    }

    private static string TruncateStreamText(string text)
    {
        if (text.Length <= MaxCommandStreamLineLength)
            return text;

        return string.Concat(text.AsSpan(0, MaxCommandStreamLineLength), " …");
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

    private async Task SendResponseAsync(string requestId, string command, AgentCommandResponse response, CancellationToken ct)
    {
        var connection = hubProvider.Connection;
        if (connection == null)
        {
            throw new InvalidOperationException(
                $"Cannot send ResponseCommand: hub connection is null. RequestId={requestId}, Command={command}.");
        }

        if (connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException(
                $"Cannot send ResponseCommand: hub connection state is {connection.State}. RequestId={requestId}, Command={command}.");
        }

        LogResponseCommandPayloadSize(requestId, command, response);

        await _responseSendPipeline.ExecuteAsync(async token =>
        {
            await connection.InvokeAsync(AgentHubMethods.ResponseCommand, requestId, response, token);
        }, ct);
    }

    /// <summary>
    /// Logs approximate UTF-8 size of the response as JSON (same shape as the hub <c>data</c> argument).
    /// The full SignalR frame is slightly larger; this is what you compare to the former 32KB receive limit.
    /// </summary>
    private void LogResponseCommandPayloadSize(string requestId, string command, AgentCommandResponse response)
    {
        try
        {
            var json = JsonSerializer.Serialize(response, ResponsePayloadSizeJsonOptions);
            var utf8Bytes = Encoding.UTF8.GetByteCount(json);

            if (utf8Bytes >= 32 * 1024)
            {
                logger.LogInformation(
                    "ResponseCommand payload ~{Utf8Bytes} UTF-8 bytes (exceeds former default SignalR MaximumReceiveMessageSize of 32KB). RequestId={RequestId}, Command={Command}",
                    utf8Bytes, requestId, command);
            }
            else
            {
                logger.LogDebug(
                    "ResponseCommand payload ~{Utf8Bytes} UTF-8 bytes. RequestId={RequestId}, Command={Command}",
                    utf8Bytes, requestId, command);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not measure ResponseCommand payload size. RequestId={RequestId}, Command={Command}", requestId, command);
        }
    }
}
