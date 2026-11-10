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
    ILogger<HookListenerHostedService> logger,
    HookRepositoryPathPolicy? pathPolicy = null) : IHostedService, IAsyncDisposable
{
    private readonly WorkerOptions _options = options.Value;
    private readonly HookRepositoryPathPolicy _pathPolicy = pathPolicy ?? new HookRepositoryPathPolicy(KnownWorkspaceRoots.Shared);
    private const int MaxConcurrentRequests = 64;

    /// <summary>Largest hook body accepted. A real payload is two ids and a path, well under 1 KB.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

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
            // A loopback caller is not trusted: any local process or a browser page can post here. A browser
            // "simple" cross-site POST cannot carry application/json, so the content type check turns it away.
            if (!IsJsonContentType(context.Request.ContentType))
            {
                Reject(context, 400, path, "the content type is not application/json");
                return;
            }

            if (context.Request.ContentLength64 > MaxBodyBytes)
            {
                Reject(context, 413, path, "the body is larger than the limit");
                return;
            }

            var body = context.Request.HasEntityBody
                ? await ReadBodyAsync(context.Request.InputStream, MaxBodyBytes, ct)
                : [];
            if (body == null)
            {
                Reject(context, 413, path, "the body is larger than the limit");
                return;
            }

            NotifyPayload? payload = null;
            try
            {
                if (body.Length > 0)
                    payload = JsonSerializer.Deserialize<NotifyPayload>(body);
            }
            catch (JsonException)
            {
                Reject(context, 400, path, "the body is not valid JSON");
                return;
            }

            if (payload == null || payload.RepositoryId == 0 || payload.WorkspaceId == 0 || string.IsNullOrWhiteSpace(payload.RepositoryPath))
            {
                Reject(context, 400, path, "the payload is incomplete");
                return;
            }

            // The path decides where git and GitVersion run, so it must be a working tree GrayMoon manages.
            if (_pathPolicy.GetRejectionReason(payload.RepositoryPath, payload.WorkspaceId, payload.RepositoryId) is { } pathRejection)
            {
                Reject(context, 400, path, pathRejection);
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

    private void Reject(HttpListenerContext context, int statusCode, string path, string reason)
    {
        logger.LogWarning("Rejected {Path} with {StatusCode}: {Reason}", path, statusCode, reason);
        context.Response.StatusCode = statusCode;
        context.Response.Close();
    }

    /// <summary>True for <c>application/json</c>, with or without parameters such as <c>; charset=utf-8</c>.</summary>
    internal static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;
        var separator = contentType.IndexOf(';');
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the whole body, or returns null once it turns out to be longer than <paramref name="maxBytes"/>. Never
    /// buffers more than one byte past the limit, whatever the declared length was (a chunked body declares none).
    /// </summary>
    internal static async Task<byte[]?> ReadBodyAsync(Stream input, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
                break;
            total += read;
        }

        return total > maxBytes ? null : buffer[..total];
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
