using System.Text;
using System.Text.Json;
using GrayMoon.Common;
using GrayMoon.Common.Ai;

namespace GrayMoon.Worker.Services.Ai;

/// <summary>A prepared prompt for one provider call. The adapter never sees a repository path.</summary>
public sealed record AiCliRequest(string SystemPrompt, string UserPrompt, string JsonSchema, string? Model);

/// <summary>Arguments, stdin and files (relative to the isolated working folder) for one CLI run.</summary>
public sealed record AiCliInvocation(IReadOnlyList<string> Arguments, byte[]? Stdin, IReadOnlyDictionary<string, string> Files);

/// <summary>The provider's JSON answer, or why there is none.</summary>
public sealed record AiCliOutcome(string? Json, AiGenerationErrorKind Error, string? Message)
{
    public static AiCliOutcome Success(string json) => new(json, AiGenerationErrorKind.None, null);

    public static AiCliOutcome Failure(AiGenerationErrorKind error, string message) => new(null, error, message);
}

/// <summary>
/// How a provider's CLI is run without tools. Each adapter uses only options verified against the installed CLI (see
/// docs/ai-pull-commit/GrayMoon-AI-Generation-Implementation-Log.md): a JSON output flag is not a sandbox, so every adapter
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
public sealed class CursorCliAdapter : IAiCliAdapter
{
    private const string RequestFile = "request.md";

    internal const string Instruction =
        "Read the file request.md in the workspace root and follow its instructions exactly. Reply with only the JSON object it asks for, with no other text before or after it.";

    /// <summary>Platforms where Cursor Agent CLI has its command sandbox ("Sandbox requires macOS or Linux").</summary>
    internal static bool SupportsSandbox => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public string ProviderId => AiProviderIds.Cursor;

    public IReadOnlyList<string> ExecutableNames { get; } = ["agent", "cursor-agent"];

    public bool HasNoTools => false;

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

    public AiCliOutcome Interpret(BoundedProcessResult result, string workDirectory)
    {
        // On failure Cursor prints no JSON and writes the error to stderr.
        if (result.ExitCode != 0 || !ClaudeCliAdapter.TryParseObject(result.StdoutText, out var root))
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
}
