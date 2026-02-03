using System.Collections.Generic;
using System.Reflection;
using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Hosted;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Results;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace GrayMoon.Agent.Cli;

internal static class RunCommandHandler
{
    /// <summary>
    /// Builds and runs the agent host with the given options (defaults from appsettings, overridden by CLI).
    /// </summary>
    public static async Task<int> RunAsync(AgentOptions options, CancellationToken cancellationToken = default)
    {
        var appConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddApplicationSettings()
            .Build();

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        Log.Information(
            "GrayMoon Agent. Version: {Version}. AppHubUrl: {AppHubUrl}, ListenPort: {ListenPort}, WorkspaceRoot: {WorkspaceRoot}, MaxConcurrentCommands: {MaxConcurrentCommands}",
            version, options.AppHubUrl, options.ListenPort, options.WorkspaceRoot, options.MaxConcurrentCommands);

        var builder = Host.CreateApplicationBuilder(args: Array.Empty<string>());
        builder.Configuration.Sources.Insert(0, new ChainedConfigurationSource { Configuration = appConfig });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.AppHubUrl)}"] = options.AppHubUrl,
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.ListenPort)}"] = options.ListenPort.ToString(),
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.WorkspaceRoot)}"] = options.WorkspaceRoot,
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.MaxConcurrentCommands)}"] = options.MaxConcurrentCommands.ToString(),
        });

        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .Enrich.WithMachineName()
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger(), dispose: true);

        builder.Services.AddSingleton<IHubConnectionProvider, HubConnectionProvider>();
        builder.Services.AddSingleton<IJobQueue, JobQueue>();
        builder.Services.AddSingleton<GitOperations>();
        builder.Services.AddSingleton<CommandJobFactory>();
        builder.Services.AddSingleton<ICommandHandlerResolver, CommandHandlerResolver>();

        builder.Services.AddSingleton<ICommandHandler<SyncRepositoryRequest, SyncRepositoryResult>, SyncRepositoryHandler>();
        builder.Services.AddSingleton<ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResult>, RefreshRepositoryVersionHandler>();
        builder.Services.AddSingleton<ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResult>, EnsureWorkspaceHandler>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResult>, GetWorkspaceRepositoriesHandler>();
        builder.Services.AddSingleton<ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResult>, GetRepositoryVersionHandler>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResult>, GetWorkspaceExistsHandler>();
        builder.Services.AddSingleton<INotifySyncHandler, NotifySyncHandler>();

        builder.Services.AddHostedService<SignalRConnectionHostedService>();
        builder.Services.AddHostedService<HookListenerHostedService>();
        builder.Services.AddHostedService<JobBackgroundService>();

        if (OperatingSystem.IsWindows())
            builder.Services.AddWindowsService();
        if (OperatingSystem.IsLinux())
            builder.Services.AddSystemd();

        var host = builder.Build();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GrayMoon.Agent.Run")
                    .LogWarning("User break (Ctrl+C) detected. Shutting down gracefully...");
                cts.Cancel();
                e.Cancel = true;
            }
        };

        await host.RunAsync(cts.Token).ConfigureAwait(false);
        return 0;
    }
}
