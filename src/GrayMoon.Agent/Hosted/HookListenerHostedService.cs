using System.Net;
using System.Text.Json;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using GrayMoon.Agent.Models;
using GrayMoon.Agent.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

public sealed class HookListenerHostedService(
    IJobQueue jobQueue,
    IOptions<AgentOptions> options,
    ILogger<HookListenerHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly AgentOptions _options = options.Value;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_options.ListenPort}/");
        _listener.Start();
        logger.LogInformation("Hook listener started on http://127.0.0.1:{Port}/", _options.ListenPort);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenTask = ListenAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(context, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in hook listener");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (context.Request.HttpMethod != "POST" || !context.Request.Url?.AbsolutePath.Equals("/notify", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        try
        {
            NotifyPayload? payload = null;
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(ct);
                payload = JsonSerializer.Deserialize<NotifyPayload>(body);
            }

            if (payload == null || payload.RepositoryId == 0 || payload.WorkspaceId == 0 || string.IsNullOrWhiteSpace(payload.RepositoryPath))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var notifyJob = new NotifySyncJob
            {
                RepositoryId = payload.RepositoryId,
                WorkspaceId = payload.WorkspaceId,
                RepositoryPath = payload.RepositoryPath
            };
            var envelope = JobEnvelope.Notify(notifyJob);
            await jobQueue.EnqueueAsync(envelope, ct);
            logger.LogDebug("Enqueued NotifySync: workspace={WorkspaceId}, repo={RepoId}", payload.WorkspaceId, payload.RepositoryId);

            context.Response.StatusCode = 202;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling /notify");
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        return _listenTask ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenTask != null)
            await _listenTask;
        _listener?.Close();
    }
}
