using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GrayMoon.Common;
using GrayMoon.Common.Ai;

namespace GrayMoon.Worker.Services.Ai;

/// <summary>A prepared prompt for one provider call. The adapter never sees a repository path.</summary>
public sealed record AiCliRequest(string SystemPrompt, string UserPrompt, string JsonSchema, string? Model)
{
    /// <summary>
    /// The user's environment variables for this provider (Worker page, Environment), already resolved by the App.
    /// Null or empty runs the CLI with the Worker's own environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>Arguments, stdin and files (relative to the isolated working folder) for one CLI run.</summary>
public sealed record AiCliInvocation(IReadOnlyList<string> Arguments, byte[]? Stdin, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// The provider's JSON answer, or why there is none. A <see cref="Warning"/> is only set with a successful
/// <see cref="Json"/> (never with a failure): the App still validates the consumer shape before showing it.
/// </summary>
public sealed record AiCliOutcome(string? Json, AiGenerationErrorKind Error, string? Message, AiGenerationWarning? Warning = null)
{
    public static AiCliOutcome Success(string json, AiGenerationWarning? warning = null) =>
        new(json, AiGenerationErrorKind.None, null, warning);

    public static AiCliOutcome Failure(AiGenerationErrorKind error, string message) =>
        new(null, error, message);

    /// <summary>
    /// Only with a failure: the environment variable the provider's adapter names for this failure, when the run did not
    /// already have it. The App adds it and runs once more; the Worker itself never retries.
    /// </summary>
    public AgentEnvironmentFix? EnvironmentFix { get; init; }
}

/// <summary>
/// How a provider's CLI is run without tools. Each adapter uses only options verified against the installed CLI: a
/// JSON output flag is not a sandbox, so every adapter
/// also switches tools off (or, where the CLI cannot, to its read-only mode) and runs in an empty folder.
/// </summary>
public interface IAiCliAdapter
{
    string ProviderId { get; }

    /// <summary>Executable names searched on PATH, preferred first.</summary>
    IReadOnlyList<string> ExecutableNames { get; }

    /// <summary>
    /// True when the CLI runs with no tools at all; false when the best it offers is a read-only mode (it can still read
    /// files). Shown to the user, never hidden.
    /// </summary>
    bool HasNoTools { get; }

    /// <summary>
    /// Environment variables this CLI is known to read and a user may want to set. Offered as suggestions on the Worker
    /// page (Environment) while the CLI is installed; none is applied until the user, or a fix, lists it.
    /// </summary>
    IReadOnlyList<KnownAgentEnvironmentVariable> KnownEnvironmentVariables { get; }

    /// <summary>
    /// The environment variable that makes a run with this failure text (stderr and stdout) work, or null when the
    /// failure is not one a variable fixes. Match one exact failure: a wrong match adds a variable the user did not ask for.
    /// </summary>
    AgentEnvironmentFix? SuggestEnvironmentFix(string failureText);

    AiCliInvocation Prepare(AiCliRequest request, string workDirectory);

    AiCliOutcome Interpret(BoundedProcessResult result, string workDirectory);
}

/// <summary>
/// Claude Code (<c>claude</c>), verified on 2.1.x: print mode with JSON output and <c>--json-schema</c> structured output;
/// <c>--tools ""</c> removes every built-in tool, <c>--safe-mode</c> drops CLAUDE.md, hooks, skills, plugins and MCP
/// servers, <c>--strict-mcp-config</c> with no config loads no MCP server, and nothing is saved as a session. The system
/// prompt is a file and the evidence goes through stdin, so no argument spans lines (safe for a <c>.cmd</c> shim).
/// </summary>
public sealed class ClaudeCliAdapter : IAiCliAdapter
{
    public string ProviderId => AiProviderIds.Claude;

    public IReadOnlyList<string> ExecutableNames { get; } = ["claude"];

    public bool HasNoTools => true;

    public IReadOnlyList<KnownAgentEnvironmentVariable> KnownEnvironmentVariables { get; } =
    [
        new(AiProviderIds.Claude, "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", "1",
            "1 keeps background shells running when Claude Code is under memory pressure (default 0)."),
    ];

    public AgentEnvironmentFix? SuggestEnvironmentFix(string failureText) => null;

    public AiCliInvocation Prepare(AiCliRequest request, string workDirectory)
    {
        List<string> arguments =
        [
            "-p",
            "--output-format", "json",
            "--tools", "",
            "--safe-mode",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--permission-prompts", "none",
            "--system-prompt-file", Path.Combine(workDirectory, "system.md"),
            "--json-schema", request.JsonSchema,
        ];
        if (request.Model is not null)
            arguments.AddRange(["--model", request.Model]);

        return new AiCliInvocation(
            arguments,
            Encoding.UTF8.GetBytes(request.UserPrompt),
            new Dictionary<string, string> { ["system.md"] = request.SystemPrompt });
    }

    public AiCliOutcome Interpret(BoundedProcessResult result, string workDirectory)
    {
        if (!TryParseObject(result.StdoutText, out var root))
            return AiCliErrors.FromProcess(result);

        using (root)
        {
            var element = root!.RootElement;
            var isError = element.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            var text = element.TryGetProperty("result", out var resultText) && resultText.ValueKind == JsonValueKind.String
                ? resultText.GetString()
                : null;
            if (isError)
                return AiCliErrors.Classify(text ?? result.Stderr);

            if (element.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
                return AiCliOutcome.Success(structured.GetRawText());

            return AiGeneratedTextParser.ExtractJsonObject(text) is { } json
                ? AiCliOutcome.Success(json)
                : AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Claude did not answer with the expected JSON.");
        }
    }

    internal static bool TryParseObject(string text, out JsonDocument? document)
    {
        document = null;
        var trimmed = text.Trim();
        // Some versions print a diagnostic line before the result object; the result is the last line.
        var lastLine = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(lastLine) || !lastLine.StartsWith('{'))
            return false;
        try
        {
            document = JsonDocument.Parse(lastLine);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return true;
            document.Dispose();
            document = null;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Codex (<c>codex exec</c>), verified on 0.162: the user's config.toml, rules and MCP servers are not loaded, the
/// session is ephemeral, the sandbox is read-only and the shell tools (<c>shell_tool</c>, <c>unified_exec</c>) and
/// other tool features are disabled, so the model has no command to run. The final message must match
/// <c>--output-schema</c> and is written to a file.
/// </summary>
public sealed class CodexCliAdapter : IAiCliAdapter
{
    private const string SchemaFile = "schema.json";
    private const string OutputFile = "answer.json";

    private static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apps", "plugins", "hooks", "multi_agent", "browser_use", "computer_use",
        "image_generation", "view_image", "goals", "memories", "skill_search", "tool_suggest",
    ];

    public string ProviderId => AiProviderIds.Codex;

    public IReadOnlyList<string> ExecutableNames { get; } = ["codex"];

    public bool HasNoTools => true;

    public IReadOnlyList<KnownAgentEnvironmentVariable> KnownEnvironmentVariables { get; } = [];

    public AgentEnvironmentFix? SuggestEnvironmentFix(string failureText) => null;

    public AiCliInvocation Prepare(AiCliRequest request, string workDirectory)
    {
        List<string> arguments =
        [
            "exec",
            "--ignore-user-config",
            "--ignore-rules",
            "--ephemeral",
            "--skip-git-repo-check",
            "--sandbox", "read-only",
        ];
        foreach (var feature in DisabledFeatures)
            arguments.AddRange(["--disable", feature]);
        arguments.AddRange(
        [
            "-c", "web_search=disabled",
            "--color", "never",
            "--output-schema", Path.Combine(workDirectory, SchemaFile),
            "-o", Path.Combine(workDirectory, OutputFile),
            "-C", workDirectory,
        ]);
        if (request.Model is not null)
            arguments.AddRange(["-m", request.Model]);
        arguments.Add("-");

        // Codex has no separate system prompt option for exec; the instructions lead the stdin prompt.
        var prompt = request.SystemPrompt + "\n\n" + request.UserPrompt;
        return new AiCliInvocation(
            arguments,
            Encoding.UTF8.GetBytes(prompt),
            new Dictionary<string, string> { [SchemaFile] = request.JsonSchema });
    }

    public AiCliOutcome Interpret(BoundedProcessResult result, string workDirectory)
    {
        if (result.ExitCode != 0)
            return AiCliErrors.FromProcess(result);

        var answerPath = Path.Combine(workDirectory, OutputFile);
        if (!File.Exists(answerPath))
            return AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Codex finished without an answer.");

        var info = new FileInfo(answerPath);
        if (info.Length > AiCliRunner.MaxAnswerBytes)
            return AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Codex answered with more text than expected.");

        var json = AiGeneratedTextParser.ExtractJsonObject(File.ReadAllText(answerPath, Encoding.UTF8));
        return json is null
            ? AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Codex did not answer with the expected JSON.")
            : AiCliOutcome.Success(json);
    }
}

/// <summary>
/// Cursor Agent CLI (<c>agent</c>, also installed as <c>cursor-agent</c>; the Cursor editor's <c>cursor</c> command is not
/// it). Per Cursor's CLI reference: print mode with JSON output, <c>--mode ask</c> (read-only: no edits, no commands),
/// the command sandbox on macOS and Linux (the only platforms that have it), MCP servers not approved, commands never auto-approved (no <c>--force</c>), and an empty folder as the
/// workspace. The CLI has no option to remove its read tools, so <see cref="HasNoTools"/> is false and the request is a
/// file in that empty folder (the CLI documents the prompt only as an argument, which cannot carry the evidence safely
/// through a <c>.cmd</c> shim).
/// </summary>
public sealed partial class CursorCliAdapter : IAiCliAdapter
{
    private const string RequestFile = "request.md";

    internal const string Instruction =
        "Read the file request.md in the workspace root and follow its instructions exactly. Reply with only the JSON object it asks for, with no other text before or after it.";

    /// <summary>Platforms where Cursor Agent CLI has its command sandbox ("Sandbox requires macOS or Linux").</summary>
    internal static bool SupportsSandbox => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public string ProviderId => AiProviderIds.Cursor;

    public IReadOnlyList<string> ExecutableNames { get; } = ["agent", "cursor-agent"];

    public bool HasNoTools => false;

    /// <summary>
    /// Makes the Node runtime bundled with Cursor Agent trust the operating system's certificate store as well as its own
    /// list. Behind a TLS-inspecting company proxy (seen on an enterprise Windows VDI) the proxy's root certificate is
    /// only in that store, and every request fails with "unable to get local issuer certificate". Verified on the
    /// bundled Node 24.5: <c>NODE_OPTIONS=--use-system-ca</c> loads the store, while <c>NODE_USE_SYSTEM_CA=1</c> is
    /// only read from Node 24.6 on and changes nothing there. Certificate checking stays on.
    /// </summary>
    internal static readonly AgentEnvironmentFix SystemCertificateStoreFix = new("NODE_OPTIONS", "--use-system-ca", AppendToExisting: true);

    [GeneratedRegex(@"unable to get local issuer certificate|UNABLE_TO_GET_ISSUER_CERT_LOCALLY", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalIssuerCertificateFailure();

    public IReadOnlyList<KnownAgentEnvironmentVariable> KnownEnvironmentVariables { get; } =
    [
        new(AiProviderIds.Cursor, SystemCertificateStoreFix.Name, SystemCertificateStoreFix.Value,
            "Trusts the Windows certificate store. Fixes \"unable to get local issuer certificate\" behind a company proxy."),
    ];

    public AgentEnvironmentFix? SuggestEnvironmentFix(string failureText) =>
        LocalIssuerCertificateFailure().IsMatch(failureText) ? SystemCertificateStoreFix : null;

    public AiCliInvocation Prepare(AiCliRequest request, string workDirectory)
    {
        List<string> arguments =
        [
            "-p",
            "--output-format", "json",
            "--mode", "ask",
            "--trust",
            "--workspace", workDirectory,
        ];
        // Cursor's command sandbox exists only on macOS and Linux and the CLI refuses to start when it is asked for
        // elsewhere. Ask mode is what keeps the run read-only on every platform (no edits, no commands); the sandbox
        // is an extra fence around commands, so it is added where the CLI supports it.
        if (SupportsSandbox)
            arguments.AddRange(["--sandbox", "enabled"]);
        if (request.Model is not null)
            arguments.AddRange(["--model", request.Model]);
        arguments.Add(Instruction);

        var document = new StringBuilder()
            .Append(request.SystemPrompt).Append("\n\n")
            .Append("Answer with a JSON object that matches this JSON Schema:\n")
            .Append(request.JsonSchema).Append("\n\n")
            .Append(request.UserPrompt)
            .ToString();
        return new AiCliInvocation(arguments, null, new Dictionary<string, string> { [RequestFile] = document });
    }

    /// <summary>
    /// Cursor runs with its read tools and often narrates before answering ("I'll read request.md...") ahead of the JSON object.
    /// The strict parse comes first; otherwise the object must be the whole remainder of the text from some opening brace, so
    /// the narration is dropped but anything after the object is still rejected.
    /// </summary>
    internal static string? ExtractAnswer(string? text)
    {
        if (AiGeneratedTextParser.ExtractJsonObject(text) is { } strict)
            return strict;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var candidate = text[start..].Trim();
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return candidate;
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// A Cursor top-level result envelope that claims success: <c>type=result</c>, <c>subtype=success</c>, and
    /// explicit <c>is_error=false</c>, with a string <c>result</c>. Used only to recover a nonzero process exit; a
    /// normal zero-exit answer still uses the looser last-line parse so ordinary Cursor output stays unchanged.
    /// </summary>
    internal static bool TryParseSuccessEnvelope(string text, out JsonDocument? document, out string? resultText)
    {
        document = null;
        resultText = null;
        if (!ClaudeCliAdapter.TryParseObject(text, out document))
            return false;

        var element = document!.RootElement;
        if (!element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "result", StringComparison.Ordinal))
        {
            document.Dispose();
            document = null;
            return false;
        }

        if (!element.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String
            || !string.Equals(subtype.GetString(), "success", StringComparison.Ordinal))
        {
            document.Dispose();
            document = null;
            return false;
        }

        if (!element.TryGetProperty("is_error", out var flag) || flag.ValueKind != JsonValueKind.False)
        {
            document.Dispose();
            document = null;
            return false;
        }

        if (!element.TryGetProperty("result", out var resultProp) || resultProp.ValueKind != JsonValueKind.String)
        {
            document.Dispose();
            document = null;
            return false;
        }

        resultText = resultProp.GetString();
        return true;
    }

    public AiCliOutcome Interpret(BoundedProcessResult result, string workDirectory)
    {
        // Nonzero exit: recover only a verified success envelope with extractable JSON. Start failure, timeout and
        // truncation never reach here (the runner rejects them first). Cancellation throws before Interpret.
        if (result.ExitCode != 0)
            return InterpretAbnormalExit(result);

        // On failure Cursor prints no JSON and writes the error to stderr.
        if (!ClaudeCliAdapter.TryParseObject(result.StdoutText, out var root))
            return AiCliErrors.FromProcess(result);

        using (root)
        {
            var element = root!.RootElement;
            var text = element.TryGetProperty("result", out var resultText) && resultText.ValueKind == JsonValueKind.String
                ? resultText.GetString()
                : null;
            if (element.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True)
                return AiCliErrors.Classify(text ?? result.Stderr);

            return ExtractAnswer(text) is { } json
                ? AiCliOutcome.Success(json)
                : AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Cursor did not answer with the expected JSON.");
        }
    }

    /// <summary>
    /// Candidate recovery only: the envelope must prove success and the embedded answer must extract to JSON.
    /// Consumer (commit/PR) shape is still validated by the App; this warning is a candidate until then.
    /// </summary>
    private static AiCliOutcome InterpretAbnormalExit(BoundedProcessResult result)
    {
        if (!TryParseSuccessEnvelope(result.StdoutText, out var root, out var text))
            return AiCliErrors.FromProcess(result);

        using (root)
        {
            if (ExtractAnswer(text) is not { } json)
                return AiCliOutcome.Failure(AiGenerationErrorKind.InvalidOutput, "Cursor did not answer with the expected JSON.");

            return AiCliOutcome.Success(json, AiGenerationWarning.ProviderExitedAfterResult(correlationId: null));
        }
    }
}
