using System.Diagnostics;
using System.Text.Json;
using GrayMoon.Abstractions.Logging;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace GrayMoon.App.Services.Worker;

/// <summary>Sends commands to the worker via SignalR and awaits responses.</summary>
public interface IWorkerBridge
{
    bool IsWorkerConnected { get; }

    /// <summary>
    /// Null when the Worker can run normal commands; otherwise the user-facing reason (not connected, or a Worker
    /// version that does not match this App). Lets an operation refuse before it changes anything.
    /// </summary>
    string? GetUnavailableReason() => IsWorkerConnected ? null : WorkerBridge.NotConnectedMessage;

    Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default);
}

public sealed class WorkerBridge(
    IHubContext<WorkerHub> hubContext,
    WorkerConnectionTracker connectionTracker,
    WorkerCommandCancelSender cancelSender,
    IOptions<WorkerBridgeOptions> options,
    ILogger<WorkerBridge> logger) : IWorkerBridge
{
    internal const string NotConnectedMessage = "Worker not connected. Start the GrayMoon Worker to sync repositories.";

    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.CommandTimeoutSeconds));

    public bool IsWorkerConnected => connectionTracker.GetWorkerConnectionId() != null;

    public string? GetUnavailableReason()
    {
        if (connectionTracker.GetWorkerConnectionId() is null)
            return NotConnectedMessage;
        return connectionTracker.State == WorkerConnectionState.VersionMismatch
            ? WorkerVersionPolicy.MismatchMessage(connectionTracker.WorkerSemVer, connectionTracker.AppSemVer)
            : null;
    }

    private void EndSelfUpdateIfStillConnected()
    {
        // A failed SelfUpdate while the worker is already offline is the install stopping the
        // service (or FailAll on disconnect) - keep the in-progress flag so the disconnect is
        // treated as installing, not as an unexpected offline.
        if (connectionTracker.State != WorkerConnectionState.Offline)
            connectionTracker.EndSelfUpdate();
    }

    public async Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
    {
        var connectionId = connectionTracker.GetWorkerConnectionId();
        if (string.IsNullOrEmpty(connectionId))
            return new WorkerCommandResponse(false, null, NotConnectedMessage);

        // Version lock: a Worker with a different GrayMoon version only runs the update and diagnostics commands.
        if (connectionTracker.State == WorkerConnectionState.VersionMismatch && !WorkerVersionPolicy.IsAllowedOnVersionMismatch(command))
        {
            logger.LogDebug(
                "Refused {Command}: Worker version {WorkerVersion} does not match App version {AppVersion}",
                command, connectionTracker.WorkerSemVer, connectionTracker.AppSemVer);
            return new WorkerCommandResponse(false, null, WorkerVersionPolicy.MismatchMessage(connectionTracker.WorkerSemVer, connectionTracker.AppSemVer));
        }

        var isSelfUpdate = command == WorkerHubMethods.SelfUpdate;
        if (isSelfUpdate)
            connectionTracker.BeginSelfUpdate();

        var requestId = Guid.NewGuid().ToString("N");
        var operationId = GrayMoonOperationContext.Current;
        var argsJson = args != null ? JsonSerializer.SerializeToElement(args) : (JsonElement?)null;

        var sink = TerminalSinkContext.Current;
        Action<WorkerCommandStreamLine>? onLine = sink != null ? sink.Append : null;

        using var timeoutCts = new CancellationTokenSource(_commandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var task = WorkerResponseDelivery.WaitAsync(requestId, linkedCts.Token, onLine);
        var sw = Stopwatch.StartNew();

        using var requestProp = LogContext.PushProperty(GrayMoonLogProperties.RequestId, requestId);
        using var operationProp = operationId is null
            ? null
            : LogContext.PushProperty(GrayMoonLogProperties.OperationId, operationId);
        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync(
                WorkerHubMethods.RequestCommand, requestId, command, argsJson, operationId, cancellationToken);
            logger.LogDebug("Sent RequestCommand: {RequestId}, {Command}", requestId, command);
            var response = await task;
            sw.Stop();
            if (!response.Success)
            {
                logger.LogWarning(
                    "Worker command {Command} ({RequestId}) failed: {Error}",
                    command, requestId, response.Error);
            }
            else
            {
                logger.LogDebug(
                    "Worker command {Command} ({RequestId}) completed in {ElapsedMs}ms",
                    command, requestId, sw.ElapsedMilliseconds);
            }

            if (isSelfUpdate && !response.Success)
                EndSelfUpdateIfStillConnected();
            return response;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Overall command timeout, not a caller-initiated cancellation: notify the worker to stop
            // work on this request, then fail fast with a normal response instead of throwing - callers
            // across the app already handle a false/error WorkerCommandResponse, so no call-site changes
            // are needed to get this "fail fast, let the user retry" behavior everywhere.
            cancelSender.NotifyCancel(requestId);
            logger.LogWarning(
                "Worker command {Command} ({RequestId}) timed out after {TimeoutSeconds}s waiting for a response",
                command, requestId, _commandTimeout.TotalSeconds);
            if (isSelfUpdate)
                EndSelfUpdateIfStillConnected();
            return new WorkerCommandResponse(false, null, $"Worker command timed out after {_commandTimeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException)
        {
            // WaitAsync cancel registration also notifies the worker; send here too in case
            // RequestCommand was delivered before SendAsync observed cancellation.
            cancelSender.NotifyCancel(requestId);
            logger.LogDebug("Worker command {Command} ({RequestId}) cancelled", command, requestId);
            if (isSelfUpdate)
                EndSelfUpdateIfStillConnected();
            throw;
        }
        catch (Exception ex)
        {
            WorkerResponseDelivery.Fail(requestId, ex);
            logger.LogError(ex, "Failed to send command {Command} ({RequestId}) to worker", command, requestId);
            if (isSelfUpdate)
                EndSelfUpdateIfStillConnected();
            return new WorkerCommandResponse(false, null, ex.Message);
        }
    }
}
