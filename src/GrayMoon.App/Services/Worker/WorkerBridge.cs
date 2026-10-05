using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Worker;

/// <summary>Sends commands to the worker via SignalR and awaits responses.</summary>
public interface IWorkerBridge
{
    bool IsWorkerConnected { get; }
    Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default);
}

public sealed class WorkerBridge(
    IHubContext<WorkerHub> hubContext,
    WorkerConnectionTracker connectionTracker,
    WorkerCommandCancelSender cancelSender,
    IOptions<WorkerBridgeOptions> options,
    ILogger<WorkerBridge> logger) : IWorkerBridge
{
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.CommandTimeoutSeconds));

    public bool IsWorkerConnected => connectionTracker.GetWorkerConnectionId() != null;

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
            return new WorkerCommandResponse(false, null, "Worker not connected. Start the GrayMoon Worker to sync repositories.");

        var isSelfUpdate = command == WorkerHubMethods.SelfUpdate;
        if (isSelfUpdate)
            connectionTracker.BeginSelfUpdate();

        var requestId = Guid.NewGuid().ToString("N");
        var argsJson = args != null ? JsonSerializer.SerializeToElement(args) : (JsonElement?)null;

        var sink = TerminalSinkContext.Current;
        Action<WorkerCommandStreamLine>? onLine = sink != null ? sink.Append : null;

        using var timeoutCts = new CancellationTokenSource(_commandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var task = WorkerResponseDelivery.WaitAsync(requestId, linkedCts.Token, onLine);

        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync(WorkerHubMethods.RequestCommand, requestId, command, argsJson, cancellationToken);
            logger.LogDebug("Sent RequestCommand: {RequestId}, {Command}", requestId, command);
            var response = await task;
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
            if (isSelfUpdate)
                EndSelfUpdateIfStillConnected();
            throw;
        }
        catch (Exception ex)
        {
            WorkerResponseDelivery.Fail(requestId, ex);
            logger.LogError(ex, "Failed to send command {Command} to worker", command);
            if (isSelfUpdate)
                EndSelfUpdateIfStillConnected();
            return new WorkerCommandResponse(false, null, ex.Message);
        }
    }
}
