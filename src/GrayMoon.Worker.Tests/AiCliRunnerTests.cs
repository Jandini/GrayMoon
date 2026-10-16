using System.Diagnostics;
using System.Text;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The CLI gateway against fake provider executables (a <c>.cmd</c> on Windows, a shell script elsewhere), so the tests
/// never call a real model: argument shape, stdin, isolation folder, output interpretation and failure handling.
/// </summary>
public sealed class AiCliRunnerTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("graymoon-fake-ai-cli-").FullName;
    private readonly RecordingRunner _runner = new(new BoundedProcessRunner(NullLogger<BoundedProcessRunner>.Instance));
    private readonly FakeLocator _locator = new();

    private static readonly AiCliRequest Request = new("System rules", "<evidence>\nchange\n</evidence>", AiOutputSchemas.Commit, null);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_bin, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AiCliRunner Runner() => new(
        [new ClaudeCliAdapter(), new CodexCliAdapter(), new CursorCliAdapter()],
        _locator,
        _runner,
        NullLogger<AiCliRunner>.Instance);

    /// <summary>A fake CLI that runs <paramref name="windows"/> (cmd) or <paramref name="unix"/> (sh) in the isolated folder.</summary>
    private void FakeCli(string name, string windows, string unix)
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_bin, name + ".cmd");
            File.WriteAllText(path, "@echo off\r\n" + windows.Replace("\n", "\r\n") + "\r\n");
        }
        else
        {
            path = Path.Combine(_bin, name);
            File.WriteAllText(path, "#!/bin/sh\n" + unix + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _locator.Paths[name] = path;
    }

    private const string ClaudeSuccessJson = """{"type":"result","is_error":false,"result":"done","structured_output":{"subject":"Add the thing","body":"Why it matters."}}""";

    [Fact]
    public async Task Claude_runs_without_tools_in_an_empty_folder_and_returns_the_structured_output()
    {
        FakeCli("claude", $"echo {ClaudeSuccessJson}", $"cat >/dev/null; echo '{ClaudeSuccessJson}'");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request with { Model = "sonnet" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.True(AiGeneratedTextParser.TryParseCommit(outcome.Json, out var message, out _));
        Assert.Equal("Add the thing\n\nWhy it matters.", message!.Message);

        var call = Assert.Single(_runner.Requests);
        var args = call.Arguments.ToList();
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--safe-mode", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.Equal(AiOutputSchemas.Commit, args[args.IndexOf("--json-schema") + 1]);
        // No argument spans lines: the system prompt is a file and the evidence goes through stdin.
        Assert.DoesNotContain(args, a => a.Contains('\n'));
        Assert.Equal(Request.UserPrompt, Encoding.UTF8.GetString(call.Stdin!));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "graymoon-ai"), call.WorkingDirectory);
        Assert.False(Directory.Exists(call.WorkingDirectory), "the isolated folder is deleted afterwards");
    }

    [Fact]
    public async Task No_model_flag_is_sent_when_the_model_is_the_default()
    {
        FakeCli("claude", $"echo {ClaudeSuccessJson}", $"cat >/dev/null; echo '{ClaudeSuccessJson}'");

        await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.DoesNotContain("--model", Assert.Single(_runner.Requests).Arguments);
    }

    [Fact]
    public async Task Claude_error_result_about_the_model_is_classified()
    {
        const string json = """{"type":"result","is_error":true,"result":"There's an issue with the selected model (nope). It may not exist or you may not have access to it."}""";
        // The apostrophe in "There's" would end the single-quoted sh string, so it is written as '\''.
        FakeCli("claude", $"echo {json}", $"cat >/dev/null; echo '{json.Replace("'", @"'\''")}'");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.UnsupportedModel, outcome.Error);
        Assert.Contains("graymoon.json", outcome.Message);
    }

    [Fact]
    public async Task Not_signed_in_is_classified()
    {
        FakeCli("claude", "echo Not logged in. Please run /login 1>&2\nexit /b 1", "cat >/dev/null; echo 'Not logged in. Please run /login' >&2; exit 1");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NotAuthenticated, outcome.Error);
    }

    [Fact]
    public async Task Text_that_is_not_the_expected_json_is_invalid_output()
    {
        FakeCli("claude", "echo Sure! Here is your commit message.", "cat >/dev/null; echo 'Sure! Here is your commit message.'");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.Json);
    }

    [Fact]
    public async Task A_slow_cli_is_killed_at_the_timeout()
    {
        FakeCli("claude", "ping -n 30 127.0.0.1 >nul", "sleep 30");
        var watch = Stopwatch.StartNew();

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Timeout, outcome.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Huge_output_is_capped_and_rejected()
    {
        FakeCli("claude",
            "for /L %%i in (1,1,20000) do echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "cat >/dev/null; yes aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | head -n 20000");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.InvalidOutput, outcome.Error);
        Assert.True(_runner.Results.Single().Stdout.Length <= AiCliRunner.MaxAnswerBytes);
    }

    [Fact]
    public async Task Missing_cli_is_not_installed()
    {
        var outcome = await Runner().GenerateAsync(AiProviderIds.Codex, Request, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NotInstalled, outcome.Error);
        Assert.Empty(_runner.Requests);
    }

    [Theory]
    [InlineData("sonnet & calc")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("a b")]
    [InlineData("m\"x")]
    public async Task A_model_that_could_inject_options_or_commands_never_reaches_a_process(string model)
    {
        FakeCli("claude", $"echo {ClaudeSuccessJson}", $"echo '{ClaudeSuccessJson}'");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Claude, Request with { Model = model }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Configuration, outcome.Error);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Codex_reads_the_answer_file_and_runs_read_only_without_user_config_or_shell_tools()
    {
        const string answer = """{"subject":"Fix the race","body":""}""";
        FakeCli("codex", $"echo {answer}> answer.json", $"cat >/dev/null; echo '{answer}' > answer.json");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Codex, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.True(AiGeneratedTextParser.TryParseCommit(outcome.Json, out var message, out _));
        Assert.Equal("Fix the race", message!.Message);

        var args = Assert.Single(_runner.Requests).Arguments.ToList();
        Assert.Equal("exec", args[0]);
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("--ephemeral", args);
        Assert.Equal("read-only", args[args.IndexOf("--sandbox") + 1]);
        Assert.Contains("shell_tool", args);
        Assert.Contains("unified_exec", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public async Task Cursor_agent_runs_in_ask_mode_and_reads_the_request_file()
    {
        const string json = """{"type":"result","subtype":"success","is_error":false,"result":"{\"title\":\"Add X\",\"description\":\"Adds X.\"}"}""";
        FakeCli("agent", $"echo {json}", $"echo '{json}'");

        var outcome = await Runner().GenerateAsync(AiProviderIds.Cursor,
            Request with { JsonSchema = AiOutputSchemas.PullRequest }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.None, outcome.Error);
        Assert.True(AiGeneratedTextParser.TryParsePullRequest(outcome.Json, out var text, out _));
        Assert.Equal("Add X", text!.Title);
        var args = Assert.Single(_runner.Requests).Arguments.ToList();
        Assert.Equal("ask", args[args.IndexOf("--mode") + 1]);
        if (CursorCliAdapter.SupportsSandbox)
            Assert.Equal("enabled", args[args.IndexOf("--sandbox") + 1]);
        else
            Assert.DoesNotContain("--sandbox", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--approve-mcps", args);
        Assert.Equal(CursorCliAdapter.Instruction, args[^1]);
        Assert.Null(_runner.Requests.Single().Stdin);
        Assert.DoesNotContain(args, a => a.Contains('\n'));
    }

    [Fact]
    public async Task Probe_reports_installed_versions_and_missing_clis()
    {
        FakeCli("claude", "echo 2.1.301 (Claude Code)", "echo '2.1.301 (Claude Code)'");

        var probe = await Runner().ProbeAsync(refresh: true, CancellationToken.None);

        var claude = Assert.Single(probe, p => p.ProviderId == AiProviderIds.Claude);
        Assert.True(claude.IsInstalled);
        Assert.Equal("2.1.301 (Claude Code)", claude.Version);
        Assert.True(claude.HasNoTools);
        var cursor = Assert.Single(probe, p => p.ProviderId == AiProviderIds.Cursor);
        Assert.False(cursor.IsInstalled);
        Assert.False(cursor.HasNoTools);
    }

    [Fact]
    public async Task Exactly_one_process_runs_per_generation_and_none_of_them_is_git()
    {
        FakeCli("claude", $"echo {ClaudeSuccessJson}", $"cat >/dev/null; echo '{ClaudeSuccessJson}'");

        await Runner().GenerateAsync(AiProviderIds.Claude, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        var call = Assert.Single(_runner.Requests);
        Assert.NotEqual("git", Path.GetFileNameWithoutExtension(call.FileName));
    }

    private sealed class FakeLocator : IAiCliExecutableLocator
    {
        public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);

        public string? Find(IReadOnlyList<string> names) =>
            names.Select(n => Paths.GetValueOrDefault(n)).FirstOrDefault(p => p is not null);
    }

    private sealed class RecordingRunner(IBoundedProcessRunner inner) : IBoundedProcessRunner
    {
        public List<BoundedProcessRequest> Requests { get; } = [];

        public List<BoundedProcessResult> Results { get; } = [];

        public async Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = await inner.RunAsync(request, cancellationToken);
            Results.Add(result);
            return result;
        }
    }
}
