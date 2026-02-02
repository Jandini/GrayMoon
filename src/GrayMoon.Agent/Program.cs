using System.Reflection;
using GrayMoon.Agent;
using GrayMoon.Agent.Handlers;
using GrayMoon.Agent.Hosted;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var appConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddApplicationSettings()
        .Build();

    var options = new AgentOptions();
    appConfig.GetSection(AgentOptions.SectionName).Bind(options);
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    Log.Information(
        "GrayMoon Agent. Version: {Version}. AppHubUrl: {AppHubUrl}, ListenPort: {ListenPort}, WorkspaceRoot: {WorkspaceRoot}, MaxConcurrentCommands: {MaxConcurrentCommands}",
        version, options.AppHubUrl, options.ListenPort, options.WorkspaceRoot, options.MaxConcurrentCommands);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.Sources.Insert(0, new ChainedConfigurationSource { Configuration = appConfig });

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(new LoggerConfiguration()
        .Enrich.WithMachineName()
        .WriteTo.Console(theme: AnsiConsoleTheme.Code)
        .CreateLogger(), dispose: true);

    builder.Services.AddSingleton<IHubConnectionProvider, HubConnectionProvider>();
    builder.Services.AddSingleton<IJobQueue, JobQueue>();
    builder.Services.AddSingleton<GitOperations>();

    builder.Services.AddHostedService<SignalRConnectionHostedService>();
    builder.Services.AddHostedService<HookListenerHostedService>();
    builder.Services.AddHostedService<JobProcessor>();

    if (OperatingSystem.IsWindows())
        builder.Services.AddWindowsService();
    if (OperatingSystem.IsLinux())
        builder.Services.AddSystemd();

    var host = builder.Build();

    using var cancellationTokenSource = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, eventArgs) =>
    {
        if (!cancellationTokenSource.IsCancellationRequested)
        {
            host.Services.GetRequiredService<ILogger<Program>>()
                .LogWarning("User break (Ctrl+C) detected. Shutting down gracefully...");
            cancellationTokenSource.Cancel();
            eventArgs.Cancel = true;
        }
    };

    await host.RunAsync(cancellationTokenSource.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
