using System.Reflection;
using GrayMoon.App.Services.Security;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Api.Endpoints;

public static class WorkerEndpoints
{
    public const string WorkerArchiveLinux = "graymoon-worker-linux.zip";
    public const string WorkerArchiveWindows = "graymoon-worker-windows.zip";

    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/worker/download", DownloadWorker);
        routes.MapGet("/api/worker/install", InstallWorker);
        routes.MapGet("/api/worker/uninstall", UninstallWorker);
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
            .Replace("{HUB_URL}", $"{baseUrl}/hub/worker")
            .Replace("{SECRET_REQUIRED}", secretRequired ? "1" : "0");

    private static IResult DownloadWorker(
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        string? platform = null)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Worker");
        var isWindows = string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase);
        var fileName = isWindows ? WorkerArchiveWindows : WorkerArchiveLinux;
        var path = Path.Combine(env.ContentRootPath, "worker", fileName);
        if (!System.IO.File.Exists(path))
        {
            logger.LogWarning("Worker archive not found at {Path}", path);
            return Results.NotFound();
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "application/zip", fileName, enableRangeProcessing: true);
    }

    private static IResult InstallWorker(
        HttpContext httpContext,
        IWebHostEnvironment env,
        ILoggerFactory loggerFactory,
        WorkerSecretService workerSecret,
        IOptions<SecurityOptions> securityOptions)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Worker");
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

    private static IResult UninstallWorker(IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Worker");

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
