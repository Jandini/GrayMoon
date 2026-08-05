using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace GrayMoon.Agent;

internal static class Extensions
{
    internal static IConfigurationBuilder AddApplicationSettings(this IConfigurationBuilder builder)
    {
        // Prefer DOTNET_ENVIRONMENT (generic host / launchSettings); fall back to ASPNETCORE_ENVIRONMENT.
        var environment =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        return builder
            .AddEmbeddedJsonFile("appsettings.json")
            .AddEmbeddedJsonFile($"appsettings.{environment}.json");
    }

    internal static IConfigurationBuilder AddEmbeddedJsonFile(this IConfigurationBuilder builder, string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fileProvider = new EmbeddedFileProvider(assembly, typeof(Extensions).Namespace);
        var fileInfo = fileProvider.GetFileInfo(name);

        if (fileInfo.Exists)
            builder.AddJsonStream(fileInfo.CreateReadStream()!);

        return builder.AddJsonFile(name, true);
    }
}
