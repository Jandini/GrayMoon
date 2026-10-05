using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Security;

/// <summary>
/// Only the real Worker may connect to <c>/hub/worker</c> or fetch a token from <c>/repos/{id}/connector</c> (F2).
///
/// - A <b>wrong</b> secret is always rejected (401).
/// - A <b>missing</b> secret is accepted with a warning until some Worker has proved it has the secret
///   (<see cref="WorkerSecretService.Seen"/>) or <c>Security:RequireWorkerSecret</c> is on; then it is 401.
///   That keeps an already-installed Worker working through the first upgrade, while a Worker that has
///   once been paired can never be impersonated by a program that simply omits the header.
/// - <c>/repos/{id}/connector</c> and <c>/api/worker/pair</c> reject any request that carries an
///   <c>Origin</c> header (403): the Worker and the install script never send one, a browser page always does.
/// </summary>
public sealed class WorkerSecretMiddleware(
    RequestDelegate next,
    WorkerSecretService secrets,
    IOptions<SecurityOptions> options,
    ILogger<WorkerSecretMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var kind = Classify(path);
        if (kind == ProtectedPath.None)
        {
            await next(context);
            return;
        }

        if ((kind is ProtectedPath.Connector or ProtectedPath.Pair) && context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (kind == ProtectedPath.Pair)
        {
            await next(context);
            return;
        }

        var check = secrets.Check(context.Request.Headers[WorkerSecretHeader.Name].ToString());
        switch (check)
        {
            case WorkerSecretCheck.Valid:
                await secrets.NoteValidSecretAsync(context.RequestAborted);
                await next(context);
                return;

            case WorkerSecretCheck.Wrong:
                logger.LogWarning("Rejected a request to {Path}: the worker secret does not match. Reinstall the Worker from GrayMoon > Worker.", path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;

            default:
                if (secrets.Seen || options.Value.RequireWorkerSecret)
                {
                    logger.LogWarning("Rejected a request to {Path}: no worker secret was presented. Reinstall the Worker from GrayMoon > Worker.", path);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (kind == ProtectedPath.WorkerHub && secrets.NoteUnsecuredWorker())
                    logger.LogWarning("A Worker connected without a worker secret. Reinstall the Worker from GrayMoon > Worker to finish securing GrayMoon.");

                await next(context);
                return;
        }
    }

    internal enum ProtectedPath
    {
        None,
        WorkerHub,
        Connector,
        Pair
    }

    internal static ProtectedPath Classify(string path)
    {
        if (WorkerHubRoutes.IsWorkerHubPath(path))
            return ProtectedPath.WorkerHub;

        if (path.Equals("/api/worker/pair", StringComparison.OrdinalIgnoreCase))
            return ProtectedPath.Pair;

        if (path.StartsWith("/repos/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/connector", StringComparison.OrdinalIgnoreCase))
            return ProtectedPath.Connector;

        return ProtectedPath.None;
    }
}
