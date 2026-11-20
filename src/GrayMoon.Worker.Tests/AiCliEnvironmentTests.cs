using System.Text;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The user's environment variables reach the provider CLI, and the one failure a variable fixes (Cursor Agent behind a
/// TLS-inspecting proxy) is reported with that variable - once, never when the run already had it.
/// </summary>
public sealed class AiCliEnvironmentTests
{
    // Stderr as Cursor Agent printed it on the enterprise Windows VDI.
    private const string IssuerFailure = "Error: unable to get local issuer certificate\n    at TLSSocket.onConnectSecure (node:_tls_wrap:1679:34) {\n  code: 'UNABLE_TO_GET_ISSUER_CERT_LOCALLY'\n}";

    private static BoundedProcessResult Process(int exitCode, string stdout, string stderr = "", bool timedOut = false, string? startFailure = null) =>
        new(exitCode, Encoding.UTF8.GetBytes(stdout), false, stderr, timedOut, startFailure);

    private static AiCliRequest Request(Dictionary<string, string>? environment = null) =>
        new("sys", "user", AiOutputSchemas.Commit, null) { Environment = environment };

    private static (AiCliRunner Runner, RecordingRunner Process) Runner(BoundedProcessResult result, params IAiCliAdapter[] adapters)
    {
        var process = new RecordingRunner(result);
        return (new AiCliRunner(adapters.Length == 0 ? [new CursorCliAdapter()] : adapters, new FixedLocator("agent.exe"), process, NullLogger<AiCliRunner>.Instance), process);
    }

    [Theory]
    [InlineData("Error: unable to get local issuer certificate")]
    [InlineData("RetriableError: [internal] Unable To Get Local Issuer Certificate")]
    [InlineData("code: 'UNABLE_TO_GET_ISSUER_CERT_LOCALLY'")]
    public void Cursor_names_the_system_certificate_store_for_the_issuer_failure_only(string failure)
    {
        var fix = new CursorCliAdapter().SuggestEnvironmentFix(failure);

        Assert.Equal(new AgentEnvironmentFix("NODE_OPTIONS", "--use-system-ca", AppendToExisting: true), fix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: not logged in")]
    [InlineData("self-signed certificate in certificate chain")]
    [InlineData("certificate has expired")]
    [InlineData("getaddrinfo ENOTFOUND api2.cursor.sh")]
    public void Cursor_names_no_fix_for_any_other_failure(string failure) =>
        Assert.Null(new CursorCliAdapter().SuggestEnvironmentFix(failure));

    [Fact]
    public void Claude_and_codex_name_no_fix_even_for_the_issuer_failure()
    {
        Assert.Null(new ClaudeCliAdapter().SuggestEnvironmentFix(IssuerFailure));
        Assert.Null(new CodexCliAdapter().SuggestEnvironmentFix(IssuerFailure));
    }

    [Fact]
    public void Each_adapter_lists_only_valid_known_variables_for_its_own_provider()
    {
        IAiCliAdapter[] adapters = [new ClaudeCliAdapter(), new CodexCliAdapter(), new CursorCliAdapter()];

        foreach (var adapter in adapters)
        {
            foreach (var known in adapter.KnownEnvironmentVariables)
            {
                Assert.Equal(adapter.ProviderId, known.ProviderId);
                Assert.Null(AgentEnvironment.NameError(known.Name));
                Assert.Null(AgentEnvironment.ValueError(known.Value));
                Assert.False(string.IsNullOrWhiteSpace(known.Description));
            }
        }

        Assert.Contains(new CursorCliAdapter().KnownEnvironmentVariables, k => k.Name == "NODE_OPTIONS" && k.Value == "--use-system-ca");
        Assert.Contains(new ClaudeCliAdapter().KnownEnvironmentVariables, k => k.Name == "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP" && k.Value == "1");
    }

    [Fact]
    public async Task The_cli_runs_with_the_users_variables_and_the_protective_ones_still_win()
    {
        var (runner, process) = Runner(Process(1, "", "boom"));

        await runner.GenerateAsync(AiProviderIds.Cursor,
            Request(new() { ["NODE_OPTIONS"] = "--use-system-ca", ["MY_TOKEN"] = "a%b&c", ["GIT_DIR"] = @"C:\elsewhere", ["term"] = "xterm" }),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        var environment = process.Requests.Single().Environment!;
        Assert.Equal("--use-system-ca", environment["NODE_OPTIONS"]);
        Assert.Equal("a%b&c", environment["MY_TOKEN"]);
        // A listed GIT_DIR or TERM cannot point the CLI at a repository or make it interactive.
        Assert.Null(environment["GIT_DIR"]);
        Assert.Equal("dumb", environment["TERM"]);
        Assert.Equal("1", environment["NO_COLOR"]);
    }

    [Fact]
    public async Task Without_variables_the_cli_runs_with_the_protective_environment_only()
    {
        var (runner, process) = Runner(Process(1, "", "boom"));

        await runner.GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        var environment = process.Requests.Single().Environment!;
        Assert.False(environment.ContainsKey("NODE_OPTIONS"));
        Assert.Equal("dumb", environment["TERM"]);
    }

    [Theory]
    [InlineData("A B", "1")]
    [InlineData("OK", "one\ntwo")]
    public async Task A_variable_the_app_should_never_send_stops_the_run_before_any_process(string name, string value)
    {
        var (runner, process) = Runner(Process(0, "{}"));

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor, Request(new() { [name] = value }), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Configuration, outcome.Error);
        Assert.Empty(process.Requests);
    }

    [Fact]
    public async Task The_issuer_failure_is_reported_with_its_fix()
    {
        var (runner, _) = Runner(Process(1, "", IssuerFailure));

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Equal(CursorCliAdapter.SystemCertificateStoreFix, outcome.EnvironmentFix);
    }

    [Fact]
    public async Task The_issuer_failure_inside_cursors_error_envelope_is_reported_with_its_fix()
    {
        var envelope = """{"type":"result","subtype":"error","is_error":true,"result":"ConnectError: [internal] unable to get local issuer certificate"}""";
        var (runner, _) = Runner(Process(0, envelope));

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.NotNull(outcome.EnvironmentFix);
    }

    [Theory]
    [InlineData("--use-system-ca")]
    [InlineData("--max-old-space-size=4096 --use-system-ca")]
    public async Task A_run_that_already_had_the_fix_reports_none_so_a_retry_cannot_repeat(string nodeOptions)
    {
        var (runner, _) = Runner(Process(1, "", IssuerFailure));

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor, Request(new() { ["node_options"] = nodeOptions }), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotEqual(AiGenerationErrorKind.None, outcome.Error);
        Assert.Null(outcome.EnvironmentFix);
    }

    [Fact]
    public async Task Other_node_options_do_not_count_as_the_fix()
    {
        var (runner, _) = Runner(Process(1, "", IssuerFailure));

        var outcome = await runner.GenerateAsync(AiProviderIds.Cursor, Request(new() { ["NODE_OPTIONS"] = "--max-old-space-size=4096" }), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(outcome.EnvironmentFix);
    }

    [Fact]
    public async Task A_timeout_a_start_failure_another_failure_and_a_success_carry_no_fix()
    {
        var timedOut = await Runner(Process(1, "", IssuerFailure, timedOut: true)).Runner
            .GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var notStarted = await Runner(Process(-1, "", IssuerFailure, startFailure: "missing")).Runner
            .GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var otherFailure = await Runner(Process(1, "", "Error: not logged in")).Runner
            .GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var success = await Runner(Process(0, """{"type":"result","subtype":"success","is_error":false,"result":"{\"subject\":\"Mention the issuer certificate\",\"body\":\"unable to get local issuer certificate\"}"}""")).Runner
            .GenerateAsync(AiProviderIds.Cursor, Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(timedOut.EnvironmentFix);
        Assert.Null(notStarted.EnvironmentFix);
        Assert.Null(otherFailure.EnvironmentFix);
        Assert.Equal(AiGenerationErrorKind.None, success.Error);
        Assert.Null(success.EnvironmentFix);
    }

    [Fact]
    public async Task The_probe_reports_each_adapters_known_variables()
    {
        var (runner, _) = Runner(Process(0, "2026.10.16\n"), new ClaudeCliAdapter(), new CursorCliAdapter());

        var providers = await runner.ProbeAsync(refresh: true, CancellationToken.None);
        var cursor = AiProviderAvailabilityDto.From(providers.Single(p => p.ProviderId == AiProviderIds.Cursor));

        Assert.True(cursor.IsInstalled);
        var known = Assert.Single(cursor.KnownEnvironmentVariables);
        Assert.Equal("NODE_OPTIONS", known.Name);
        Assert.Equal("--use-system-ca", known.Value);
        Assert.False(string.IsNullOrWhiteSpace(known.Description));
    }

    private static AiCliAvailability Availability(IAiCliAdapter adapter, bool installed) =>
        new(adapter.ProviderId, installed, installed ? "agent.exe" : null, installed ? "1.0" : null, adapter.HasNoTools, null)
        {
            KnownEnvironmentVariables = adapter.KnownEnvironmentVariables,
        };

    [Fact]
    public void A_known_variable_the_user_already_set_on_the_host_is_reported_with_its_value()
    {
        var asked = new List<string>();
        var dto = AiProviderAvailabilityDto.From(Availability(new CursorCliAdapter(), installed: true), name =>
        {
            asked.Add(name);
            return name == "NODE_OPTIONS" ? "--max-old-space-size=4096" : null;
        });

        var known = Assert.Single(dto.KnownEnvironmentVariables);
        Assert.Equal("--max-old-space-size=4096", known.CurrentValue);
        // The suggestion's own value is unchanged, and only the adapter's variables are ever read from the host.
        Assert.Equal("--use-system-ca", known.Value);
        Assert.Equal(["NODE_OPTIONS"], asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two\nlines")]
    public void A_variable_that_is_not_set_or_not_usable_is_reported_as_not_configured(string? hostValue)
    {
        var dto = AiProviderAvailabilityDto.From(Availability(new ClaudeCliAdapter(), installed: true), _ => hostValue);

        Assert.Null(Assert.Single(dto.KnownEnvironmentVariables).CurrentValue);
    }

    [Fact]
    public void A_cli_that_is_not_installed_reports_no_host_values_and_the_host_is_not_read()
    {
        var read = false;
        var dto = AiProviderAvailabilityDto.From(Availability(new CursorCliAdapter(), installed: false), _ =>
        {
            read = true;
            return "--anything";
        });

        Assert.Null(Assert.Single(dto.KnownEnvironmentVariables).CurrentValue);
        Assert.False(read);
    }

    [Fact]
    public void The_host_reader_returns_what_the_worker_process_has()
    {
        var name = "GRAYMOON_TEST_HOST_VARIABLE_" + Guid.NewGuid().ToString("N");
        Assert.Null(HostEnvironmentVariables.Read(name));

        Environment.SetEnvironmentVariable(name, "from-the-process");
        try
        {
            Assert.Equal("from-the-process", HostEnvironmentVariables.Read(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private sealed class FixedLocator(string path) : IAiCliExecutableLocator
    {
        public string? Find(IReadOnlyList<string> names) => path;
    }

    private sealed class RecordingRunner(BoundedProcessResult result) : IBoundedProcessRunner
    {
        public List<BoundedProcessRequest> Requests { get; } = [];

        public Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
