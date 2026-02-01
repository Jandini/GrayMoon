using GrayMoon.Agent;
using GrayMoon.Agent.Handlers;
using GrayMoon.Agent.Hosted;
using GrayMoon.Agent.Hub;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services));

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
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
