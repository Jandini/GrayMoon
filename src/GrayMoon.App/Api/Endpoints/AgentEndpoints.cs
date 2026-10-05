using System.Reflection;
using GrayMoon.App.Services.Security;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Api.Endpoints;

public static class AgentEndpoints
{
    public const string AgentArchiveLinux = "graymoon-worker-linux.zip";
    public const string AgentArchiveWindows = "graymoon-worker-windows.zip";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/worker/download", DownloadAgent);
        routes.MapGet("/api/worker/install", InstallAgent);
        routes.MapGet("/api/worker/uninstall", UninstallAgent);
        routes.MapPost("/api/worker/pair", PairWorker);
        return routes;
    }

    /// <summary>Exchanges the one-time pairing code shown on the Worker page for the Worker secret (F2).
    /// A wrong, spent or expired code is 401. Browser requests (any Origin header) are rejected earlier.</summary>
    internal static IResult PairWorker(WorkerPairRequest? request, WorkerPairingService pairing)
    {
        var secret = pairing.TryRedeem(request?.Code);
        return secret is null
            ? Results.Unauthorized()
            : Results.Ok(new WorkerPairResponse(secret));
    }

    public sealed record WorkerPairRequest(string? Code);

    public sealed record WorkerPairResponse(string Secret);

    /// <summary>Fills the install script placeholders. <paramref name="secretRequired"/> only says whether a Worker
    /// without a secret would be refused; the secret itself never goes into this unauthenticated script.</summary>
    internal static string RenderInstallScript(string template, string baseUrl, bool secretRequired) =>
        template
            .Replace("{DOWNLOAD_URL}", $"{baseUrl}/api/worker/download?platform=windows")
            .Replace("{BASE_URL}", baseUrl)
            .Replace("{HUB_URL}", $"{baseUrl}/hub/agent")
            .Replace("{SECRET_REQUIRED}", secretRequired ? "1" : "0");

    private static IResult DownloadAgent(
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        string? platform = null)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Agent");
        var isWindows = string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase);
        var fileName = isWindows ? AgentArchiveWindows : AgentArchiveLinux;
        var path = Path.Combine(env.ContentRootPath, "agent", fileName);
        if (!System.IO.File.Exists(path))
        {
            logger.LogWarning("Agent archive not found at {Path}", path);
            return Results.NotFound();
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "application/zip", fileName, enableRangeProcessing: true);
    }

    private static IResult InstallAgent(
        HttpContext httpContext,
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        WorkerSecretService workerSecret,
        IOptions<SecurityOptions> securityOptions)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Agent");
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "GrayMoon.App.Resources.install-worker.ps1";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogError("Embedded resource {ResourceName} not found", resourceName);
                return Results.NotFound("Installation script not found");
            }

            using var reader = new StreamReader(stream);
            var script = RenderInstallScript(
                reader.ReadToEnd(),
                baseUrl,
                secretRequired: workerSecret.Seen || securityOptions.Value.RequireWorkerSecret);

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
            var resourceName = "GrayMoon.App.Resources.uninstall-worker.ps1";

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
