using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GrayMoon.Common;

namespace GrayMoon.Agent.Cli;

internal static class AgentCli
{
    /// <summary>
    /// Builds the root command with verbs: run (default), install, uninstall.
    /// Shared options (hub-url, listen-port, workspace-root, concurrency) are defined once and attached to run and install.
    /// </summary>
    public static RootCommand Build()
    {
        var root = new RootCommand("GrayMoon Agent: host-side worker for git and repository operations.");

        var runCommand = new Command("run", "Run the agent (default). Uses appsettings with optional CLI overrides.");
        AgentCliOptions.AddTo(runCommand);
        runCommand.SetAction(RunAsync);
        root.Subcommands.Add(runCommand);

        var installCommand = new Command("install", "Install the agent as a Windows service or systemd unit.");
        AgentCliOptions.AddTo(installCommand);
        installCommand.SetAction(InstallAsync);
        root.Subcommands.Add(installCommand);

        var uninstallCommand = new Command("uninstall", "Remove the agent Windows service or systemd unit.");
        uninstallCommand.SetAction(UninstallAsync);
        root.Subcommands.Add(uninstallCommand);

        return root;
    }

    private static async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var appConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddApplicationSettings()
            .Build();

        var options = new AgentOptions();
        appConfig.GetSection(AgentOptions.SectionName).Bind(options);
        AgentCliOptions.ApplyTo(options, parseResult);

        return await RunCommandHandler.RunAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> InstallAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ICommandLineService, CommandLineService>()
            .BuildServiceProvider();
        var commandLine = services.GetRequiredService<ICommandLineService>();
        return await InstallCommandHandler.InstallAsync(parseResult, cancellationToken, commandLine).ConfigureAwait(false);
    }

    private static async Task<int> UninstallAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ICommandLineService, CommandLineService>()
            .BuildServiceProvider();
        var commandLine = services.GetRequiredService<ICommandLineService>();
        return await UninstallCommandHandler.UninstallAsync(cancellationToken, commandLine).ConfigureAwait(false);
    }
}
