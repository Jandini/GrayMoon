using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GrayMoon.Common;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Cli;

internal static class WorkerCli
{
    /// <summary>
    /// Builds the root command with verbs: run (default), install, uninstall, stop, start.
    /// </summary>
    public static RootCommand Build()
    {
        var root = new RootCommand("GrayMoon Worker: host-side worker for git and repository operations.");

        var runCommand = new Command("run", "Run the worker (default). Uses appsettings with optional CLI overrides.");
        WorkerCliOptions.AddTo(runCommand);
        runCommand.SetAction(RunAsync);
        root.Subcommands.Add(runCommand);

        var installCommand = new Command("install", "Install the worker as a Windows service or systemd unit.");
        WorkerCliOptions.AddTo(installCommand);
        installCommand.Options.Add(WorkerCliOptions.Account);
        installCommand.Options.Add(WorkerCliOptions.NonInteractive);
        installCommand.SetAction(InstallAsync);
        root.Subcommands.Add(installCommand);

        var uninstallCommand = new Command("uninstall", "Remove the worker Windows service or systemd unit.");
        uninstallCommand.SetAction(UninstallAsync);
        root.Subcommands.Add(uninstallCommand);

        var stopCommand = new Command("stop", "Stop the GrayMoon Worker Windows service.");
        stopCommand.SetAction((_, ct) => StopCommandHandler.StopAsync(ct));
        root.Subcommands.Add(stopCommand);

        var startCommand = new Command("start", "Start the GrayMoon Worker Windows service.");
        startCommand.SetAction((_, ct) => StartCommandHandler.StartAsync(ct));
        root.Subcommands.Add(startCommand);

        return root;
    }

    private static async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var appConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddApplicationSettings()
            .Build();

        var options = new WorkerOptions();
        appConfig.GetSection(WorkerOptions.SectionName).Bind(options);
        WorkerCliOptions.ApplyTo(options, parseResult);

        return await RunCommandHandler.RunAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> InstallAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var commandLine = BuildCommandLineService();
        return await InstallCommandHandler.InstallAsync(parseResult, cancellationToken, commandLine).ConfigureAwait(false);
    }

    private static async Task<int> UninstallAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var commandLine = BuildCommandLineService();
        return await UninstallCommandHandler.UninstallAsync(cancellationToken, commandLine).ConfigureAwait(false);
    }

    private static ICommandLineService BuildCommandLineService()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ICommandLineService, CommandLineService>()
            .BuildServiceProvider();
        return services.GetRequiredService<ICommandLineService>();
    }
}
