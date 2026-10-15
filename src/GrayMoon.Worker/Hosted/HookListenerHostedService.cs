using System.Net;
using System.Text.Json;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

public sealed class HookListenerHostedService(
    IJobQueue jobQueue,
    IWorktreeCreateHookDeferral hookDeferral,
    IOptions<WorkerOptions> options,
    ILogger<HookListenerHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly WorkerOptions _options = options.Value;
    private const int MaxConcurrentRequests = 64;

    private readonly SemaphoreSlim _inFlight = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_options.ListenPort}/hook/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.LogError(ex,
                "Could not start the git hook listener on 127.0.0.1:{Port}. Another program may be using that port; free it or change the Worker listen port.",
                _options.ListenPort);
            _listener.Close();
            _listener = null;
            throw;
        }
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
                if (!_inFlight.Wait(0, CancellationToken.None))
                {
                    // Hooks are tiny local POSTs; this many in flight means something is flooding the port.
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    continue;
                }

                _ = HandleThenReleaseAsync(context, ct);
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

    private async Task HandleThenReleaseAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleRequestAsync(context, ct);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var hookKind = path.ToLowerInvariant() switch
        {
            "/hook/checkout" => (NotifyHookKind?)NotifyHookKind.Checkout,
            "/hook/commit" => NotifyHookKind.Commit,
            "/hook/merge" => NotifyHookKind.Merge,
            "/hook/push" => NotifyHookKind.Push,
            "/hook/refs" => NotifyHookKind.Refs,
            _ => null
        };

        if (context.Request.HttpMethod != "POST" || hookKind == null)
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
                RepositoryPath = payload.RepositoryPath,
                HookKind = hookKind.Value
            };
            if (hookDeferral.TryDefer(notifyJob))
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            var envelope = JobEnvelope.Notify(notifyJob);
            await jobQueue.EnqueueAsync(envelope, ct);
            logger.LogDebug("Enqueued {HookKind} hook: workspace={WorkspaceId}, repo={RepoId}", hookKind, payload.WorkspaceId, payload.RepositoryId);

            context.Response.StatusCode = 202;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling {Path}", path);
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
        _listener?.Close();
        if (_listenTask != null)
        {
            try
            {
                await _listenTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
            {
                // Expected while the listener is being closed.
            }
        }
    }
}
