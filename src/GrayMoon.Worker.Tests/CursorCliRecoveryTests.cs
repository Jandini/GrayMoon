using System.Text;
using System.Text.Json;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Cursor-only recovery when the process exits nonzero but stdout holds a verified success envelope and extractable JSON.
/// Uses canned <see cref="BoundedProcessResult"/> values so exit codes and stderr are exact.
/// </summary>
public sealed class CursorCliRecoveryTests
{
    private static readonly CursorCliAdapter Adapter = new();

    private const string CommitJson = """{"subject":"Fix the race","body":"Why it matters."}""";
    private const string VdiStderr = "'-p' is not recognized as an internal or external command,\r\noperable program or batch file.";

    private static string SuccessEnvelope(string resultPayload) =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = resultPayload,
        });

    private static BoundedProcessResult Process(
        int exitCode,
        string stdout,
        string stderr = "",
        bool timedOut = false,
        bool truncated = false,
        string? startFailure = null) =>
        new(exitCode, Encoding.UTF8.GetBytes(stdout), truncated, stderr, timedOut, startFailure);

    [Fact]
    public void Zero_exit_with_valid_envelope_is_normal_success_without_warning()
    {
        var outcome = Adapter.Interpret(Process(0, SuccessEnvelope(CommitJson)), ".");

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Warning);
        Assert.Equal(CommitJson, outcome.Json);
    }

    [Fact]
    public void Nonzero_exit_with_valid_success_envelope_and_commit_json_is_recovered_with_warning()
    {
        var narrative = "I'll read `request.md` and follow its instructions.\n" + CommitJson;
        var outcome = Adapter.Interpret(Process(1, SuccessEnvelope(narrative), VdiStderr), ".");

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.NotNull(outcome.Warning);
        Assert.Equal(AiGenerationWarningCodes.ProviderExitedAfterResult, outcome.Warning!.Code);
        Assert.True(AiGeneratedTextParser.TryParseCommit(outcome.Json, out var message, out _));
        Assert.Equal("Fix the race", message!.Subject);
    }

    [Fact]
    public void Vdi_stderr_category_is_windows_argument_treated_as_command()
    {
        Assert.Equal(
            AiCliDiagnostics.StderrWindowsArgumentTreatedAsCommand,
            AiCliDiagnostics.ClassifyStderr(VdiStderr));
    }

    [Fact]
    public void Nonzero_exit_with_success_envelope_but_unusable_result_text_is_invalid_output()
    {
        var outcome = Adapter.Interpret(Process(1, SuccessEnvelope("Sure, here is a message without JSON.")), ".");

        Assert.Equal(AiGenerationErrorKind.InvalidOutput, outcome.Error);
        Assert.Null(outcome.Warning);
        Assert.Null(outcome.Json);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error","is_error":false,"result":"{\"subject\":\"x\",\"body\":\"\"}"}""")]
    [InlineData("""{"type":"assistant","subtype":"success","is_error":false,"result":"{\"subject\":\"x\",\"body\":\"\"}"}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"boom"}""")]
    [InlineData("""{"type":"result","subtype":"success","result":"{\"subject\":\"x\",\"body\":\"\"}"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Nonzero_exit_without_verified_success_envelope_is_not_recovered(string stdout)
    {
        var outcome = Adapter.Interpret(Process(1, stdout, VdiStderr), ".");

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Warning);
        Assert.Null(outcome.Json);
    }

    [Fact]
    public void False_positive_json_in_narration_without_envelope_is_rejected()
    {
        var stdout = "debug {\"subject\":\"nope\",\"body\":\"\"}\nand more\n";
        var outcome = Adapter.Interpret(Process(1, stdout), ".");

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public void Multiple_result_lines_use_the_last_line_only()
    {
        var decoy = """{"type":"result","subtype":"success","is_error":false,"result":"{\"subject\":\"Decoy\",\"body\":\"\"}"}""";
        var real = SuccessEnvelope(CommitJson);
        var outcome = Adapter.Interpret(Process(1, decoy + "\n" + real), ".");

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.NotNull(outcome.Warning);
        Assert.True(AiGeneratedTextParser.TryParseCommit(outcome.Json, out var message, out _));
        Assert.Equal("Fix the race", message!.Subject);
    }

    [Fact]
    public async Task Runner_does_not_recover_timeout_even_when_stdout_looks_usable()
    {
        var stub = new StubRunner(Process(1, SuccessEnvelope(CommitJson), timedOut: true));
        var runner = new AiCliRunner([Adapter], new FixedLocator("agent.exe"), stub, NullLogger<AiCliRunner>.Instance);

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor,
            new AiCliRequest("sys", "user", AiOutputSchemas.Commit, null),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Timeout, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Runner_does_not_recover_truncated_stdout()
    {
        var stub = new StubRunner(Process(1, SuccessEnvelope(CommitJson), truncated: true));
        var runner = new AiCliRunner([Adapter], new FixedLocator("agent.exe"), stub, NullLogger<AiCliRunner>.Instance);

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor,
            new AiCliRequest("sys", "user", AiOutputSchemas.Commit, null),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.InvalidOutput, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Runner_does_not_recover_start_failure()
    {
        var stub = new StubRunner(Process(-1, "", startFailure: "missing"));
        var runner = new AiCliRunner([Adapter], new FixedLocator("agent.exe"), stub, NullLogger<AiCliRunner>.Instance);

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor,
            new AiCliRequest("sys", "user", AiOutputSchemas.Commit, null),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NotInstalled, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Runner_stamps_operation_id_on_recovered_warning_and_logs_without_prompt_or_answer()
    {
        var logs = new CapturingLogger<AiCliRunner>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fakeExe = Path.Combine(localAppData, "cursor-agent-test", "agent.cmd");
        var stub = new StubRunner(Process(1, SuccessEnvelope(CommitJson), VdiStderr));
        var runner = new AiCliRunner([Adapter], new FixedLocator(fakeExe), stub, logs);

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor,
            new AiCliRequest("SECRET_PROMPT_SYSTEM", "SECRET_PROMPT_USER\ndiff --git a/secret", AiOutputSchemas.Commit, "model-x"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.NotNull(outcome.Warning?.CorrelationId);

        var joined = string.Join('\n', logs.Entries.Select(e => e.Message));
        Assert.Contains("RecoveredNonZeroExit", joined);
        Assert.Contains("WindowsArgumentTreatedAsCommand", joined);
        Assert.Contains(outcome.Warning!.CorrelationId!, joined);
        Assert.DoesNotContain("SECRET_PROMPT", joined);
        Assert.DoesNotContain("Fix the race", joined);
        Assert.DoesNotContain("diff --git", joined);
        Assert.DoesNotContain(fakeExe, joined);
        Assert.Contains("PathCategory=LocalAppData", joined);
        Assert.Contains("ExecutableKind=cmd", joined);
        Assert.Contains("LauncherChain=WindowsBatchShim", joined);
    }

    [Fact]
    public async Task Claude_nonzero_exit_is_unchanged_and_not_recovered_as_cursor_warning()
    {
        // Claude already ignores exit code when JSON parses; ensure we do not attach a Cursor recovery warning.
        var envelope = """{"type":"result","is_error":false,"result":"done","structured_output":{"subject":"Add the thing","body":"Why."}}""";
        var stub = new StubRunner(Process(1, envelope, "some stderr"));
        var runner = new AiCliRunner(
            [new ClaudeCliAdapter(), Adapter],
            new FixedLocator("claude.exe"),
            stub,
            NullLogger<AiCliRunner>.Instance);

        var outcome = await runner.GenerateAsync(AiProviderIds.Claude,
            new AiCliRequest("sys", "user", AiOutputSchemas.Commit, null),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Codex_nonzero_exit_is_still_a_failure()
    {
        var stub = new StubRunner(Process(1, "", "failed"));
        var runner = new AiCliRunner(
            [new CodexCliAdapter()],
            new FixedLocator("codex.exe"),
            stub,
            NullLogger<AiCliRunner>.Instance);

        var outcome = await runner.GenerateAsync(AiProviderIds.Codex,
            new AiCliRequest("sys", "user", AiOutputSchemas.Commit, null),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Warning);
    }

    private sealed class FixedLocator(string path) : IAiCliExecutableLocator
    {
        public string? Find(IReadOnlyList<string> names) => path;
    }

    private sealed class StubRunner(BoundedProcessResult result) : IBoundedProcessRunner
    {
        public Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
