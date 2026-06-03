using System.CommandLine;

namespace GrayMoon.Agent.Cli;

internal static class AgentCliOptions
{
    public static readonly Option<string?> HubUrl = new("--hub-url", "-u")
    {
        Description = "SignalR hub URL the agent connects to",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Option<int> ListenPort = new("--listen-port", "-p")
    {
        Description = "HTTP port for hook notifications (/notify)",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Option<int> Concurrency = new("--concurrency", "-c")
    {
        Description = "Max concurrent command executions",
        Arity = ArgumentArity.ZeroOrOne
    };

    // Install-only: not added to the run command.
    public static readonly Option<string?> Account = new("--account")
    {
        Description = "Windows account to run the service as (default: current user)",
        Arity = ArgumentArity.ZeroOrOne
    };

    /// <summary>
    /// Adds the shared run/install options to a command.
    /// </summary>
    public static void AddTo(Command command)
    {
        command.Options.Add(HubUrl);
        command.Options.Add(ListenPort);
        command.Options.Add(Concurrency);
    }

    private static bool WasPassed(ParseResult parseResult, Option option)
    {
        var result = parseResult.GetResult(option);
        return result is { Implicit: false };
    }

    /// <summary>
    /// Applies parsed CLI values over the given options; only overrides when explicitly provided.
    /// </summary>
    public static void ApplyTo(AgentOptions options, ParseResult parseResult)
    {
        if (WasPassed(parseResult, HubUrl) && parseResult.GetValue(HubUrl) is { } hubUrl)
            options.AppHubUrl = hubUrl;
        if (WasPassed(parseResult, ListenPort))
            options.ListenPort = parseResult.GetValue(ListenPort);
        if (WasPassed(parseResult, Concurrency))
            options.MaxConcurrentCommands = parseResult.GetValue(Concurrency);
    }

    /// <summary>
    /// Builds the run-verb argument string for service registration, e.g. "run --hub-url ...".
    /// </summary>
    public static string BuildRunArguments(ParseResult parseResult)
    {
        var parts = new List<string> { "run" };
        if (WasPassed(parseResult, HubUrl) && parseResult.GetValue(HubUrl) is { } hubUrl)
            parts.Add($"--hub-url \"{hubUrl}\"");
        if (WasPassed(parseResult, ListenPort))
            parts.Add($"--listen-port {parseResult.GetValue(ListenPort)}");
        if (WasPassed(parseResult, Concurrency))
            parts.Add($"--concurrency {parseResult.GetValue(Concurrency)}");
        return string.Join(" ", parts);
    }
}
