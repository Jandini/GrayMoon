using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Hub;
using GrayMoon.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

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
    IReadJobQueue readJobQueue,
    IDiffJobQueue diffJobQueue,
    IAiJobQueue aiJobQueue,
    CommandJobFactory commandJobFactory,
    CommandJobCancellationRegistry cancellationRegistry,
    IWorkerSecretProvider workerSecretProvider,
    IOptions<WorkerOptions> options,
    ILogger<SignalRConnectionHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly WorkerOptions _options = options.Value;
    private readonly CommandRequestIntake _intake = new(cancellationRegistry, logger);
    private HubConnection? _connection;
    private CancellationTokenSource? _hostCts;
    private CancellationToken _shutdownToken;

    /// <summary>The reconnect loop started by the last close, or null when none was started.</summary>
    internal Task? ReconnectTask { get; private set; }

    private async Task ReportSemVerAsync(CancellationToken cancellationToken)
    {
        if (_connection == null) return;

        var workerSemVer = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        try
        {
            await _connection.InvokeAsync(WorkerHubMethods.ReportSemVer, workerSemVer, cancellationToken);
            logger.LogInformation("Reported worker SemVer: {SemVer}", workerSemVer);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report SemVer to hub");
        }
    }

    /// <summary>The first connect loop started by <see cref="StartAsync"/>, or null before the start.</summary>
    internal Task? ConnectTask { get; private set; }

    /// <summary>
    /// Builds the connection and returns. The connect loop runs in the background: the host starts its hosted
    /// services one after another, so waiting here for the App would keep the hook listener and the job pools from
    /// starting for as long as the App is down.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        BuildConnection(cancellationToken);
        var shutdownToken = _shutdownToken;
        ConnectTask = Task.Run(() => ConnectWithRetryAsync(shutdownToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Creates the connection and its handlers without connecting.</summary>
    internal void BuildConnection(CancellationToken cancellationToken)
    {
        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Captured once: the token stays readable (and cancelled) after StopAsync disposes the source.
        var shutdownToken = _shutdownToken = _hostCts.Token;

        var workerSecret = workerSecretProvider.GetSecret();
        if (string.IsNullOrEmpty(workerSecret))
            logger.LogWarning("No worker secret is configured. Connecting without one; reinstall the Worker from GrayMoon > Worker to finish securing GrayMoon.");

        _connection = new HubConnectionBuilder()
            .WithUrl(_options.AppHubUrl, httpOptions =>
            {
                if (!string.IsNullOrEmpty(workerSecret))
                    httpOptions.Headers[WorkerSecretHeader.Name] = workerSecret;
            })
            .WithAutomaticReconnect(new FiveSecondRetryPolicy())
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            })
            .Build();

        _connection.On<string, string, JsonElement?, string?>(WorkerHubMethods.RequestCommand, (requestId, command, args, operationId) =>
        {
            logger.LogDebug("RequestCommand {RequestId} received: {Command}", requestId, command);
            logger.LogTrace("RequestCommand {RequestId} request content: {Args}", requestId, args.HasValue ? args.Value.GetRawText() : "null");
            IJobQueue targetQueue = WorkerCommandPoolRouter.Resolve(command) switch
            {
                WorkerCommandPool.Diff => diffJobQueue,
                WorkerCommandPool.Read => readJobQueue,
                WorkerCommandPool.Ai => aiJobQueue,
                _ => jobQueue,
            };

            // The client runs one handler at a time, so this must return without waiting for room in the queue:
            // a wait here would hold back the CancelCommand that is meant to end it.
            return _intake.AcceptAsync(
                requestId,
                command,
                () => commandJobFactory.CreateCommandJob(requestId, command, args, operationId),
                targetQueue,
                shutdownToken,
                SendCommandResponseAsync);
        });

        _connection.On<string>(WorkerHubMethods.CancelCommand, requestId =>
        {
            logger.LogDebug("CancelCommand {RequestId} received", requestId);
            cancellationRegistry.Cancel(requestId);
            return Task.CompletedTask;
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

        _connection.Closed += HandleClosedAsync;

        ((HubConnectionProvider)hubProvider).Connection = _connection;
    }

    /// <summary>
    /// Starts the reconnect loop after the connection closes, unless the Worker is stopping. The loop observes the
    /// shutdown token (cancelled by <see cref="StopAsync"/>), not the token passed to <see cref="StartAsync"/>, which
    /// only covers startup and is never cancelled afterwards.
    /// </summary>
    internal Task HandleClosedAsync(Exception? error)
    {
        var shutdownToken = _shutdownToken;
        if (shutdownToken.IsCancellationRequested)
            return Task.CompletedTask;

        logger.LogWarning(error, "Connection closed. Will attempt to reconnect in 5 seconds...");
        // Start a background task to reconnect if automatic reconnect didn't work
        ReconnectTask = Task.Run(async () => await ReconnectLoopAsync(shutdownToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task SendCommandResponseAsync(string requestId, WorkerCommandResponse response)
        => _connection?.State == HubConnectionState.Connected
            ? _connection.InvokeAsync(WorkerHubMethods.ResponseCommand, requestId, response, CancellationToken.None)
            : Task.CompletedTask;

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
                if (cancellationToken.IsCancellationRequested)
                    return;

                logger.LogWarning(ex, "Failed to connect to hub. Retrying in 5s...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
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

                HubConnectionState state;
                try
                {
                    state = _connection.State;
                }
                catch (ObjectDisposedException)
                {
                    logger.LogDebug("Connection was disposed. Exiting reconnect loop.");
                    return;
                }

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
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                logger.LogDebug("Connection was disposed. Exiting reconnect loop.");
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconnection attempt failed. Will retry in 5 seconds...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hostCts != null)
        {
            await _hostCts.CancelAsync();
            _hostCts.Dispose();
            _hostCts = null;
        }

        if (_connection != null)
        {
            await _connection.StopAsync(cancellationToken);
            logger.LogInformation("Disconnected from hub");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hostCts != null)
        {
            _hostCts.Dispose();
            _hostCts = null;
        }

        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}

/// <summary>
/// Takes a RequestCommand off the SignalR handler and into its pool's queue. The .NET client dispatches one
/// invocation at a time, so <see cref="AcceptAsync"/> never waits: when the bounded queue is full the wait for room
/// continues in the background, in arrival order, and the handler returns. A CancelCommand that arrives meanwhile
/// ends that wait and the caller gets "Command cancelled.".
/// </summary>
internal sealed class CommandRequestIntake(CommandJobCancellationRegistry cancellationRegistry, ILogger logger)
{
    /// <summary>
    /// Returns a completed task in every case. <paramref name="respond"/> reports a request that never reached the
    /// queue (cancelled, or the enqueue failed).
    /// </summary>
    public Task AcceptAsync(
        string requestId,
        string command,
        Func<Jobs.JobEnvelope> createEnvelope,
        IJobQueue targetQueue,
        CancellationToken hostToken,
        Func<string, WorkerCommandResponse, Task> respond)
    {
        // Runs synchronously up to the queue write, so requests keep their arrival order; whatever is still
        // pending after that (a full queue, a response send) finishes on its own and reports its own outcome.
        _ = AcceptCoreAsync(requestId, command, createEnvelope, targetQueue, hostToken, respond);
        return Task.CompletedTask;
    }

    private async Task AcceptCoreAsync(
        string requestId,
        string command,
        Func<Jobs.JobEnvelope> createEnvelope,
        IJobQueue targetQueue,
        CancellationToken hostToken,
        Func<string, WorkerCommandResponse, Task> respond)
    {
        try
        {
            var jobCts = cancellationRegistry.Register(requestId, hostToken);
            if (jobCts.IsCancellationRequested)
            {
                logger.LogDebug("RequestCommand {RequestId} already cancelled; skipping enqueue", requestId);
                cancellationRegistry.Unregister(requestId);
                await RespondAsync(requestId, new WorkerCommandResponse(false, null, "Command cancelled."), respond);
                return;
            }

            await targetQueue.EnqueueAsync(createEnvelope(), jobCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("RequestCommand {RequestId} enqueue cancelled", requestId);
            cancellationRegistry.Unregister(requestId);
            await RespondAsync(requestId, new WorkerCommandResponse(false, null, "Command cancelled."), respond);
        }
        catch (Exception ex)
        {
            cancellationRegistry.Unregister(requestId);
            logger.LogError(ex, "Failed to enqueue command {Command} (RequestId={RequestId})", command, requestId);
            await RespondAsync(requestId, new WorkerCommandResponse(false, null, ex.Message), respond);
        }
    }

    private async Task RespondAsync(string requestId, WorkerCommandResponse response, Func<string, WorkerCommandResponse, Task> respond)
    {
        try
        {
            await respond(requestId, response);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send response for {RequestId}", requestId);
        }
    }
}