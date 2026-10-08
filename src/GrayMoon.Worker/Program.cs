using GrayMoon.Worker.Cli;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    var effectiveArgs = cliArgs.Length == 0 || cliArgs[0] is not ("run" or "install" or "uninstall" or "stop" or "start" or "inspect-locks")
        ? new[] { "run" }.Concat(cliArgs).ToArray()
        : cliArgs;
    var rootCommand = WorkerCli.Build();
    var exitCode = await rootCommand.Parse(effectiveArgs).InvokeAsync().ConfigureAwait(false);
    if (exitCode != 0)
        Environment.Exit(exitCode);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
