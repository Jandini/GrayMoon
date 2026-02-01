using GrayMoon.App.Services;
using Microsoft.AspNetCore.Routing;

namespace GrayMoon.App.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/workspaces/{workspaceId:int}/sync-events", StreamSyncEvents);
        return routes;
    }

    private static async Task StreamSyncEvents(
        int workspaceId,
        HttpContext context,
        IWorkspaceSyncNotifier notifier,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Workspace");
        logger.LogInformation("GET /api/workspaces/{WorkspaceId}/sync-events (SSE) connected", workspaceId);

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.FlushAsync(context.RequestAborted);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                await notifier.WaitForSyncAsync(workspaceId, context.RequestAborted);
                await context.Response.WriteAsync("data: refresh\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        finally
        {
            logger.LogDebug("GET /api/workspaces/{WorkspaceId}/sync-events disconnected", workspaceId);
        }
    }
}
