using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.Ai;

/// <summary>What the Worker knows about one provider's CLI on this machine, as the Worker's own account sees it.</summary>
public sealed record AiCliAvailability(string ProviderId, bool IsInstalled, string? ExecutablePath, string? Version, bool HasNoTools, string? Problem);

/// <summary>
/// The single gateway that runs a provider CLI: resolves the executable on PATH, creates an empty private folder as the
/// working directory, writes the adapter's files there, runs the process with an argument list, stdin, a timeout and
/// capped output, interprets the answer, and deletes the folder. The CLI never sees a repository path, and prompts and
/// answers are never logged; only timings, sizes and the outcome are.
/// </summary>
public sealed class AiCliRunner(IEnumerable<IAiCliAdapter> adapters, IAiCliExecutableLocator locator, IBoundedProcessRunner runner, ILogger<AiCliRunner> logger)
{
    internal const int MaxAnswerBytes = 262_144;
    private const int MaxStderrBytes = 16_384;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyDictionary<string, IAiCliAdapter> _adapters = adapters.ToDictionary(a => a.ProviderId, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (AiCliAvailability Availability, DateTimeOffset At)> _probeCache = new(StringComparer.Ordinal);

    // Variables that would point a child at a repository or make it interactive.
    private static readonly IReadOnlyDictionary<string, string?> ChildEnvironment = new Dictionary<string, string?>
    {
        ["GIT_DIR"] = null,
        ["GIT_WORK_TREE"] = null,
        ["GIT_INDEX_FILE"] = null,
        ["GIT_OBJECT_DIRECTORY"] = null,
        ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
        ["GIT_COMMON_DIR"] = null,
        ["NO_COLOR"] = "1",
        ["TERM"] = "dumb",
    };

    public async Task<IReadOnlyList<AiCliAvailability>> ProbeAsync(bool refresh, CancellationToken cancellationToken)
    {
        var tasks = AiProviderIds.All
            .Where(_adapters.ContainsKey)
            .Select(id => ProbeOneAsync(_adapters[id], refresh, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    public async Task<AiCliOutcome> GenerateAsync(string providerId, AiCliRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(providerId, out var adapter))
            return AiCliOutcome.Failure(AiGenerationErrorKind.Configuration, $"Unknown AI provider \"{providerId}\".");
        if (request.Model is not null && !AiProviderConfiguration.IsValidModel(request.Model))
            return AiCliOutcome.Failure(AiGenerationErrorKind.Configuration, "The model name is not valid.");

        var executable = locator.Find(adapter.ExecutableNames);
        if (executable is null)
            return AiCliOutcome.Failure(AiGenerationErrorKind.NotInstalled,
                $"{AiProviderIds.DisplayName(providerId)} CLI ({adapter.ExecutableNames[0]}) was not found on the Worker's PATH.");

        var workDirectory = Path.Combine(Path.GetTempPath(), "graymoon-ai", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var invocation = adapter.Prepare(request, workDirectory);
            foreach (var (name, content) in invocation.Files)
                await File.WriteAllTextAsync(Path.Combine(workDirectory, name), content, new UTF8Encoding(false), cancellationToken);

            // The arguments never carry the prompt (it goes through stdin or a file), so the call itself can be shown.
            ReportToOverlay(WorkerCommandStreamKind.CommandLine, $"$ {Path.GetFileName(executable)} {FormatArguments(invocation.Arguments)}");
            var result = await runner.RunAsync(new BoundedProcessRequest(executable, invocation.Arguments)
            {
                WorkingDirectory = workDirectory,
                Stdin = invocation.Stdin,
                MaxStdoutBytes = MaxAnswerBytes,
                MaxStderrBytes = MaxStderrBytes,
                Timeout = timeout,
                Environment = ChildEnvironment,
            }, cancellationToken);

            var outcome = result switch
            {
                { StartFailure: not null } => AiCliOutcome.Failure(AiGenerationErrorKind.NotInstalled, $"{AiProviderIds.DisplayName(providerId)} CLI could not be started."),
                { TimedOut: true } => AiCliOutcome.Failure(AiGenerationErrorKind.Timeout, $"{AiProviderIds.DisplayName(providerId)} did not answer within {timeout.TotalSeconds:0} seconds."),
                { StdoutTruncated: true } => AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, $"{AiProviderIds.DisplayName(providerId)} answered with more output than expected."),
                _ => adapter.Interpret(result, workDirectory),
            };

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            ReportToOverlay(
                outcome.Error == AiGenerationErrorKind.None ? WorkerCommandStreamKind.Stdout : WorkerCommandStreamKind.Stderr,
                outcome.Error == AiGenerationErrorKind.None
                    ? $"{AiProviderIds.DisplayName(providerId)} answered in {seconds:0.0} s."
                    : $"{AiProviderIds.DisplayName(providerId)} failed after {seconds:0.0} s: {outcome.Message}");

            logger.LogInformation(
                "AI CLI call finished. Provider={Provider} ModelSet={ModelSet} PromptChars={PromptChars} ElapsedMs={ElapsedMs} ExitCode={ExitCode} Outcome={Outcome}",
                providerId, request.Model is not null, request.SystemPrompt.Length + request.UserPrompt.Length,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.ExitCode, outcome.Error);

            // The prompt is never logged, but a reply the adapter could not read is the one thing that explains the failure:
            // log its size and a short, single-line start and end of what the CLI printed (the model's answer, not the input).
            if (outcome.Error is AiGenerationErrorKind.InvalidOutput or AiGenerationErrorKind.ProviderFailed)
                logger.LogWarning(
                    "AI CLI reply was not usable. Provider={Provider} Error={Error} ExitCode={ExitCode} StdoutChars={StdoutChars} StdoutTruncated={Truncated} StderrChars={StderrChars} Stderr={Stderr} StdoutHead={Head} StdoutTail={Tail}",
                    providerId, outcome.Error, result.ExitCode, result.StdoutText.Length, result.StdoutTruncated, result.Stderr.Length,
                    Preview(result.Stderr, 300), Preview(result.StdoutText, 600), Preview(result.StdoutText.Length > 600 ? result.StdoutText[^300..] : string.Empty, 300));
            return outcome;
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    private async Task<AiCliAvailability> ProbeOneAsync(IAiCliAdapter adapter, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh
            && _probeCache.TryGetValue(adapter.ProviderId, out var cached)
            && DateTimeOffset.UtcNow - cached.At < ProbeCacheLifetime)
            return cached.Availability;

        var executable = locator.Find(adapter.ExecutableNames);
        AiCliAvailability availability;
        if (executable is null)
        {
            availability = new AiCliAvailability(adapter.ProviderId, false, null, null, adapter.HasNoTools,
                $"{adapter.ExecutableNames[0]} was not found on the Worker's PATH.");
        }
        else
        {
            var result = await runner.RunAsync(new BoundedProcessRequest(executable, ["--version"])
            {
                WorkingDirectory = Path.GetTempPath(),
                MaxStdoutBytes = 4_096,
                Timeout = ProbeTimeout,
                Environment = ChildEnvironment,
            }, cancellationToken);
            var version = result.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            availability = result.ExitCode == 0 && !string.IsNullOrEmpty(version)
                ? new AiCliAvailability(adapter.ProviderId, true, executable, version.Length > 80 ? version[..80] : version, adapter.HasNoTools, null)
                : new AiCliAvailability(adapter.ProviderId, false, executable, null, adapter.HasNoTools,
                    $"{Path.GetFileName(executable)} --version failed; reinstall or update the CLI.");
        }

        _probeCache[adapter.ProviderId] = (availability, DateTimeOffset.UtcNow);
        return availability;
    }

    /// <summary>Streams a line to the overlay terminal of the App job that sent the command, when there is one.</summary>
    private static void ReportToOverlay(WorkerCommandStreamKind kind, string text) =>
        CommandLineStreamAmbient.Current.Value?.Invoke(new CommandLineStreamEvent(kind, text));

    /// <summary>Shell-like echo of an argument list: empty or spaced arguments are quoted so the line reads as typed.</summary>
    internal static string FormatArguments(IEnumerable<string> arguments) =>
        LogSafe.ForLog(string.Join(' ', arguments.Select(a =>
            a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c == '"')
                ? $"\"{a.Replace("\"", "\\\"")}\""
                : a)));

    private static string Preview(string text, int maxChars)
    {
        var oneLine = text.Replace("\r", "").Replace("\n", "\\n");
        return oneLine.Length <= maxChars ? oneLine : oneLine[..maxChars] + "...";
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete the AI CLI working folder {Directory}", directory);
        }
    }
}

/// <summary>Turns a failed CLI run into a short, redacted, user-facing reason.</summary>
public static partial class AiCliErrors
{
    [GeneratedRegex(@"(?i)(not logged in|log ?in|logged out|unauthori[sz]ed|authenticat|\b401\b|invalid api key|api key|credential|session expired|token expired)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthPattern();

    [GeneratedRegex(@"(?i)model.{0,80}(not found|invalid|unknown|not available|unsupported|not supported|does not exist|may not exist|no access|unrecognized)|unrecognized_model", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"(?i)(unknown option|unexpected argument|unrecognized (option|argument)|invalid value for|unknown feature|unknown command)", RegexOptions.CultureInvariant)]
    private static partial Regex CliPattern();

    public static AiCliOutcome FromProcess(BoundedProcessResult result) =>
        Classify(string.IsNullOrWhiteSpace(result.Stderr) ? result.StdoutText : result.Stderr + "\n" + result.StdoutText);

    public static AiCliOutcome Classify(string? text)
    {
        var sample = (text ?? string.Empty).Length > 4_096 ? text![..4_096] : text ?? string.Empty;
        var kind = ModelPattern().IsMatch(sample) ? AiGenerationErrorKind.UnsupportedModel
            : CliPattern().IsMatch(sample) ? AiGenerationErrorKind.UnsupportedCli
            : AuthPattern().IsMatch(sample) ? AiGenerationErrorKind.NotAuthenticated
            : AiGenerationErrorKind.ProviderFailed;
        return AiCliOutcome.Failure(kind, Summarize(sample, kind));
    }

    private static string Summarize(string text, AiGenerationErrorKind kind)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("warning", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        line = EvidenceRedactor.LooksSecret(line) ? EvidenceRedactor.Placeholder : line;
        if (line.Length > 300)
            line = line[..300];

        var prefix = kind switch
        {
            AiGenerationErrorKind.NotAuthenticated => "The CLI is not signed in. Sign in with the CLI on the Worker machine, then retry.",
            AiGenerationErrorKind.UnsupportedModel => "The CLI does not accept the configured model. Check ai.providers in graymoon.json.",
            AiGenerationErrorKind.UnsupportedCli => "This CLI version does not support the options GrayMoon uses. Update the CLI.",
            _ => "The CLI failed.",
        };
        return line.Length == 0 ? prefix : $"{prefix} ({line})";
    }
}

/// <summary>Resolves a CLI executable by name, the way the Worker's account would start it.</summary>
public interface IAiCliExecutableLocator
{
    string? Find(IReadOnlyList<string> names);
}

/// <summary>
/// Finds a CLI on the Worker's PATH, in PATH order like a shell does. On Windows, within one folder a native <c>.exe</c>
/// wins over a <c>.cmd</c>/<c>.bat</c> shim; PowerShell-only shims are not runnable without a shell and are skipped.
/// </summary>
public sealed class PathAiCliExecutableLocator : IAiCliExecutableLocator
{
    public string? Find(IReadOnlyList<string> names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0)
            .ToList();
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];

        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                foreach (var extension in extensions)
                {
                    try
                    {
                        var candidate = Path.Combine(directory, name + extension);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry; skip it.
                    }
                }
            }
        }

        return null;
    }
}
