using Microsoft.AspNetCore.Routing;

namespace GrayMoon.App.Api.Endpoints;

public static class AgentEndpoints
{
    public const string AgentFileNameLinux = "graymoon-agent";
    public const string AgentFileNameWindows = "graymoon-agent.exe";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/agent/download", DownloadAgent);
        return routes;
    }

    private static IResult DownloadAgent(
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        string? platform = null)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Agent");
        var isWindows = string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase);
        var fileName = isWindows ? AgentFileNameWindows : AgentFileNameLinux;
        var path = Path.Combine(env.ContentRootPath, "agent", fileName);
        if (!System.IO.File.Exists(path))
        {
            logger.LogWarning("Agent binary not found at {Path}", path);
            return Results.NotFound();
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
    }
}
