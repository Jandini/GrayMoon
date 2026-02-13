using System.Reflection;

namespace GrayMoon.App.Api.Endpoints;

public static class AgentEndpoints
{
    public const string AgentFileNameLinux = "graymoon-agent";
    public const string AgentFileNameWindows = "graymoon-agent.exe";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/agent/download", DownloadAgent);
        routes.MapGet("/api/agent/install", InstallAgent);
        routes.MapGet("/api/agent/uninstall", UninstallAgent);
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

    private static IResult InstallAgent(HttpContext httpContext, IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Agent");
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var downloadUrl = $"{baseUrl}/api/agent/download?platform=windows";
        var hubUrl = $"{baseUrl}/hub/agent";
        
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "GrayMoon.App.Resources.install-agent.ps1";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogError("Embedded resource {ResourceName} not found", resourceName);
                return Results.NotFound("Installation script not found");
            }
            
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();
            
            // Replace placeholders
            script = script.Replace("{DOWNLOAD_URL}", downloadUrl);
            script = script.Replace("{BASE_URL}", baseUrl);
            script = script.Replace("{HUB_URL}", hubUrl);
            
            return Results.Content(script, "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load installation script");
            return Results.Problem("Failed to load installation script");
        }
    }

    private static IResult UninstallAgent(IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Agent");
        
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "GrayMoon.App.Resources.uninstall-agent.ps1";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogError("Embedded resource {ResourceName} not found", resourceName);
                return Results.NotFound("Uninstallation script not found");
            }
            
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();
            
            return Results.Content(script, "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load uninstallation script");
            return Results.Problem("Failed to load uninstallation script");
        }
    }
}
