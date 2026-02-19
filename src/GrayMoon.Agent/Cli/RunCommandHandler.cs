using System.Collections.Generic;
using System.Reflection;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Hosted;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.File;
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

        // Determine log file path
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrayMoon", "logs");
        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory, "graymoon-agent-.log");

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithMachineName()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{MachineName}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger(), dispose: true);

        builder.Services.AddSingleton<IHubConnectionProvider, HubConnectionProvider>();
        builder.Services.AddSingleton<IJobQueue, JobQueue>();
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<ICsProjFileParser, CsProjFileParser>();
        builder.Services.AddSingleton<ICsProjFileService, CsProjFileService>();
        builder.Services.AddSingleton<CommandJobFactory>();
        builder.Services.AddSingleton<ICommandDispatcher, CommandDispatcher>();

        builder.Services.AddSingleton<ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>, SyncRepositoryCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>, RefreshRepositoryVersionCommand>();
        builder.Services.AddSingleton<ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse>, EnsureWorkspaceCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>, GetWorkspaceRepositoriesCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse>, GetRepositoryVersionCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse>, GetWorkspaceExistsCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceRootRequest, GetWorkspaceRootResponse>, GetWorkspaceRootCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetHostInfoRequest, GetHostInfoResponse>, GetHostInfoCommand>();
        builder.Services.AddSingleton<ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse>, SyncRepositoryDependenciesCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse>, RefreshRepositoryProjectsCommand>();
        builder.Services.AddSingleton<ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse>, CommitSyncRepositoryCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetBranchesRequest, GetBranchesResponse>, GetBranchesCommand>();
        builder.Services.AddSingleton<ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse>, CheckoutBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse>, SyncToDefaultBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse>, RefreshBranchesCommand>();
        builder.Services.AddSingleton<ICommandHandler<CreateBranchRequest, CreateBranchResponse>, CreateBranchCommand>();
        builder.Services.AddSingleton<INotifySyncHandler, NotifySyncCommand>();

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
