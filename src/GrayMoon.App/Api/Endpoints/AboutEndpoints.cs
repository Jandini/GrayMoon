using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace GrayMoon.App.Api.Endpoints;

public static class AboutEndpoints
{
    public static IEndpointRouteBuilder MapAboutEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/about", GetAbout);
        return routes;
    }

    private static string GetAbout(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.About");
        logger.LogInformation("GET /api/about called");
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"GrayMoon {version}";
    }
}
