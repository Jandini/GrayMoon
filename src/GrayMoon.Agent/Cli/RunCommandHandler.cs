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
            "GrayMoon Agent. Version: {Version}. AppHubUrl: {AppHubUrl}, ListenPort: {ListenPort}, MaxConcurrentCommands: {MaxConcurrentCommands}",
            version, options.AppHubUrl, options.ListenPort, options.MaxConcurrentCommands);

        var builder = Host.CreateApplicationBuilder(args: Array.Empty<string>());
        builder.Configuration.Sources.Insert(0, new ChainedConfigurationSource { Configuration = appConfig });

        // Derive a default AppApiBaseUrl from AppHubUrl when not explicitly configured.
        // Example: AppHubUrl = "http://host.docker.internal:8384/hub/agent"
        // -> AppApiBaseUrl = "http://host.docker.internal:8384"
        string? defaultAppApiBaseUrl = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.AppHubUrl))
            {
                var hubUri = new Uri(options.AppHubUrl, UriKind.Absolute);
                var builderUri = new UriBuilder(hubUri.Scheme, hubUri.Host, hubUri.Port);
                defaultAppApiBaseUrl = builderUri.Uri.ToString().TrimEnd('/');
            }
        }
        catch
        {
            // Fallback: leave AppApiBaseUrl unset; token provider will log and skip remote calls when missing.
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.AppHubUrl)}"] = options.AppHubUrl,
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.AppApiBaseUrl)}"] = defaultAppApiBaseUrl,
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.ListenPort)}"] = options.ListenPort.ToString(),
            [$"{AgentOptions.SectionName}:{nameof(AgentOptions.MaxConcurrentCommands)}"] = options.MaxConcurrentCommands.ToString(),
        });

        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

        // Determine log file path
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrayMoon", "logs");
        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory, "graymoon-agent-.log");

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .MinimumLevel.Debug()
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
        builder.Services.AddSingleton<TrackedJobQueue>();
        builder.Services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<TrackedJobQueue>());
        builder.Services.AddSingleton<IAgentQueueTracker>(sp => sp.GetRequiredService<TrackedJobQueue>());
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IAgentTokenProvider, AgentTokenProvider>();
        builder.Services.AddSingleton<ICsProjFileParser, CsProjFileParser>();
        builder.Services.AddSingleton<ICsProjFileService, CsProjFileService>();
        builder.Services.AddSingleton<IWorkspaceFileSearchService, WorkspaceFileSearchService>();
        builder.Services.AddSingleton<CommandJobFactory>();
        builder.Services.AddSingleton<ICommandDispatcher, CommandDispatcher>();

        builder.Services.AddSingleton<ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>, SyncRepositoryCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>, RefreshRepositoryVersionCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetCommitCountsRequest, GetCommitCountsResponse>, GetCommitCountsCommand>();
        builder.Services.AddSingleton<ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse>, EnsureWorkspaceCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>, GetWorkspaceRepositoriesCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse>, GetRepositoryVersionCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse>, GetWorkspaceExistsCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetHostInfoRequest, GetHostInfoResponse>, GetHostInfoCommand>();
        builder.Services.AddSingleton<ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse>, SyncRepositoryDependenciesCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse>, RefreshRepositoryProjectsCommand>();
        builder.Services.AddSingleton<ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse>, CommitSyncRepositoryCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetBranchesRequest, GetBranchesResponse>, GetBranchesCommand>();
        builder.Services.AddSingleton<ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse>, CheckoutBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse>, SyncToDefaultBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse>, RefreshBranchesCommand>();
        builder.Services.AddSingleton<ICommandHandler<CreateBranchRequest, CreateBranchResponse>, CreateBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<SetUpstreamBranchRequest, SetUpstreamBranchResponse>, SetUpstreamBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<DeleteBranchRequest, DeleteBranchResponse>, DeleteBranchCommand>();
        builder.Services.AddSingleton<ICommandHandler<StageAndCommitRequest, StageAndCommitResponse>, StageAndCommitCommand>();
        builder.Services.AddSingleton<ICommandHandler<PushRepositoryRequest, PushRepositoryResponse>, PushRepositoryCommand>();
        builder.Services.AddSingleton<ICommandHandler<SearchFilesRequest, SearchFilesResponse>, SearchFilesCommand>();
        builder.Services.AddSingleton<ICommandHandler<UpdateFileVersionsRequest, UpdateFileVersionsResponse>, UpdateFileVersionsCommand>();
        builder.Services.AddSingleton<ICommandHandler<GetFileContentsRequest, GetFileContentsResponse>, GetFileContentsCommand>();
        builder.Services.AddSingleton<ICommandHandler<ValidatePathRequest, ValidatePathResponse>, ValidatePathCommand>();
        builder.Services.AddSingleton<CheckoutHookSyncCommand>();
        builder.Services.AddSingleton<CommitHookSyncCommand>();
        builder.Services.AddSingleton<MergeHookSyncCommand>();
        builder.Services.AddSingleton<INotifySyncHandler, HookSyncDispatcher>();

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
