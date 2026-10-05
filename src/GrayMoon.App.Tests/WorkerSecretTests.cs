using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>F2: only the real Worker may connect to the hub or fetch a connector token.</summary>
public class WorkerSecretTests
{
    private sealed class FakeSeenStore(bool seen = false) : IWorkerSecretSeenStore
    {
        public bool Seen { get; private set; } = seen;

        public int Writes { get; private set; }

        public Task<bool> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Seen);

        public Task SetAsync(bool value, CancellationToken cancellationToken)
        {
            Seen = value;
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class TempSecretFolder : IDisposable
    {
        public TempSecretFolder()
        {
            Directory = Path.Combine(Path.GetTempPath(), "gm-workersecret-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string SecretPath => Path.Combine(Directory, WorkerSecretService.SecretFileName);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static WorkerSecretService CreateService(TempSecretFolder folder, IWorkerSecretSeenStore? store = null) =>
        new(folder.SecretPath, store ?? new FakeSeenStore(), NullLogger<WorkerSecretService>.Instance);

    private sealed class Pipeline(WorkerSecretService secrets, SecurityOptions options)
    {
        public bool NextCalled { get; private set; }

        public async Task<DefaultHttpContext> SendAsync(string path, string? secret = null, string? origin = null, string method = "GET")
        {
            var middleware = new WorkerSecretMiddleware(
                _ =>
                {
                    NextCalled = true;
                    return Task.CompletedTask;
                },
                secrets,
                Options.Create(options),
                NullLogger<WorkerSecretMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = method;
            if (secret is not null)
                context.Request.Headers[WorkerSecretHeader.Name] = secret;
            if (origin is not null)
                context.Request.Headers.Origin = origin;

            await middleware.InvokeAsync(context);
            return context;
        }
    }

    // --- Secret file --------------------------------------------------------------

    [Fact]
    public void New_install_generates_a_secret_file_and_reuses_it_on_the_next_start()
    {
        using var folder = new TempSecretFolder();

        var first = CreateService(folder);
        Assert.True(File.Exists(folder.SecretPath));
        Assert.Equal(first.Secret, File.ReadAllText(folder.SecretPath).Trim());

        var second = CreateService(folder);
        Assert.Equal(first.Secret, second.Secret);
    }

    [Fact]
    public void Generated_secret_is_a_header_safe_token()
    {
        using var folder = new TempSecretFolder();
        var secret = CreateService(folder).Secret;

        Assert.True(secret.Length >= 32);
        Assert.All(secret, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void Secret_file_path_sits_next_to_the_database()
    {
        var path = WorkerSecretService.ResolveSecretFilePath("Data Source=" + Path.Combine("some", "dir", "graymoon.db"));

        Assert.Equal(Path.Combine("some", "dir", WorkerSecretService.SecretFileName), path);
    }

    // --- Check ---------------------------------------------------------------------

    [Fact]
    public void Check_distinguishes_missing_wrong_and_valid()
    {
        using var folder = new TempSecretFolder();
        var service = CreateService(folder);

        Assert.Equal(WorkerSecretCheck.Missing, service.Check(null));
        Assert.Equal(WorkerSecretCheck.Missing, service.Check(""));
        Assert.Equal(WorkerSecretCheck.Wrong, service.Check("not-the-secret"));
        Assert.Equal(WorkerSecretCheck.Wrong, service.Check(service.Secret + "x"));
        Assert.Equal(WorkerSecretCheck.Valid, service.Check(service.Secret));
    }

    // --- Seen flag -----------------------------------------------------------------

    [Fact]
    public async Task First_valid_secret_is_persisted_as_seen_once()
    {
        using var folder = new TempSecretFolder();
        var store = new FakeSeenStore();
        var service = CreateService(folder, store);
        await service.InitializeAsync();
        Assert.False(service.Seen);

        await service.NoteValidSecretAsync();
        await service.NoteValidSecretAsync();

        Assert.True(service.Seen);
        Assert.True(store.Seen);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Seen_flag_survives_a_restart_when_the_secret_file_is_still_there()
    {
        using var folder = new TempSecretFolder();
        CreateService(folder);

        var restarted = CreateService(folder, new FakeSeenStore(seen: true));
        await restarted.InitializeAsync();

        Assert.True(restarted.Seen);
    }

    [Fact]
    public async Task Missing_secret_file_at_startup_generates_a_new_secret_and_resets_seen()
    {
        using var folder = new TempSecretFolder();
        var firstSecret = CreateService(folder).Secret;
        File.Delete(folder.SecretPath);

        var store = new FakeSeenStore(seen: true);
        var restarted = CreateService(folder, store);
        await restarted.InitializeAsync();

        Assert.NotEqual(firstSecret, restarted.Secret);
        Assert.False(restarted.Seen);
        Assert.False(store.Seen);
    }

    // --- Middleware: connector endpoint and hub ---------------------------------

    [Theory]
    [InlineData("/repos/7/connector")]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Right_secret_passes_and_marks_seen(string path)
    {
        using var folder = new TempSecretFolder();
        var store = new FakeSeenStore();
        var service = CreateService(folder, store);
        var pipeline = new Pipeline(service, new SecurityOptions());

        await pipeline.SendAsync(path, secret: service.Secret);

        Assert.True(pipeline.NextCalled);
        Assert.True(service.Seen);
        Assert.True(store.Seen);
    }

    [Theory]
    [InlineData("/repos/7/connector")]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    [InlineData("/hub/worker/negotiate")]
    public async Task Wrong_secret_is_401(string path)
    {
        using var folder = new TempSecretFolder();
        var pipeline = new Pipeline(CreateService(folder), new SecurityOptions());

        var context = await pipeline.SendAsync(path, secret: "wrong-secret");

        Assert.False(pipeline.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/repos/7/connector")]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Missing_secret_is_accepted_while_not_required_and_never_seen(string path)
    {
        using var folder = new TempSecretFolder();
        var pipeline = new Pipeline(CreateService(folder), new SecurityOptions { RequireWorkerSecret = false });

        var context = await pipeline.SendAsync(path);

        Assert.True(pipeline.NextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/repos/7/connector")]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Missing_secret_is_401_when_required(string path)
    {
        using var folder = new TempSecretFolder();
        var pipeline = new Pipeline(CreateService(folder), new SecurityOptions { RequireWorkerSecret = true });

        var context = await pipeline.SendAsync(path);

        Assert.False(pipeline.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/repos/7/connector")]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Missing_secret_is_401_once_a_worker_has_presented_the_secret(string path)
    {
        using var folder = new TempSecretFolder();
        var service = CreateService(folder);
        await service.NoteValidSecretAsync();
        var pipeline = new Pipeline(service, new SecurityOptions { RequireWorkerSecret = false });

        var context = await pipeline.SendAsync(path);

        Assert.False(pipeline.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/hub/worker")]
    [InlineData("/hub/agent")]   // legacy path kept for already-installed Workers
    public async Task Hub_without_secret_flags_an_unsecured_worker_until_it_disconnects_or_is_secured(string hubPath)
    {
        using var folder = new TempSecretFolder();
        var service = CreateService(folder);
        var pipeline = new Pipeline(service, new SecurityOptions());

        await pipeline.SendAsync(hubPath + "/negotiate", method: "POST");
        Assert.True(service.UnsecuredWorkerConnected);

        service.NoteWorkerDisconnected();
        Assert.False(service.UnsecuredWorkerConnected);

        await pipeline.SendAsync(hubPath);
        Assert.True(service.UnsecuredWorkerConnected);

        await pipeline.SendAsync(hubPath, secret: service.Secret);
        Assert.False(service.UnsecuredWorkerConnected);
    }

    [Fact]
    public void Unsecured_worker_is_noted_once_per_connection()
    {
        using var folder = new TempSecretFolder();
        var service = CreateService(folder);

        Assert.True(service.NoteUnsecuredWorker());
        Assert.False(service.NoteUnsecuredWorker());
    }

    [Fact]
    public async Task Connector_request_with_any_origin_is_403_even_with_the_right_secret()
    {
        using var folder = new TempSecretFolder();
        var service = CreateService(folder);
        var pipeline = new Pipeline(service, new SecurityOptions());

        var context = await pipeline.SendAsync("/repos/7/connector", secret: service.Secret, origin: "http://localhost:8384");

        Assert.False(pipeline.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/workspaces")]
    [InlineData("/hubs/workspace-sync")]
    [InlineData("/_blazor")]
    [InlineData("/repos/7/branches")]
    public async Task Other_paths_are_never_touched(string path)
    {
        using var folder = new TempSecretFolder();
        var pipeline = new Pipeline(CreateService(folder), new SecurityOptions { RequireWorkerSecret = true });

        var context = await pipeline.SendAsync(path, method: "POST");

        Assert.True(pipeline.NextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Pair_endpoint_with_an_origin_is_403_and_without_one_passes()
    {
        using var folder = new TempSecretFolder();
        var pipeline = new Pipeline(CreateService(folder), new SecurityOptions { RequireWorkerSecret = true });

        var browser = await pipeline.SendAsync("/api/worker/pair", origin: "http://localhost:8384", method: "POST");
        Assert.False(pipeline.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, browser.Response.StatusCode);

        await pipeline.SendAsync("/api/worker/pair", method: "POST");
        Assert.True(pipeline.NextCalled);
    }

    // --- Pairing code ----------------------------------------------------------------

    private static WorkerPairingService CreatePairing(WorkerSecretService secrets, TimeProvider? time = null) =>
        new(secrets, NullLogger<WorkerPairingService>.Instance, time);

    [Fact]
    public void Pairing_code_is_valid_once()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);
        var pairing = CreatePairing(secrets);

        var code = pairing.CreateCode();

        Assert.Equal(secrets.Secret, pairing.TryRedeem(code.Code));
        Assert.Null(pairing.TryRedeem(code.Code));
    }

    [Fact]
    public void Pairing_code_accepts_spaces_and_dashes()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);
        var pairing = CreatePairing(secrets);

        var code = pairing.CreateCode().Code;
        var typed = code[..4] + "-" + code[4..];

        Assert.Equal(secrets.Secret, pairing.TryRedeem(" " + typed + " "));
    }

    [Fact]
    public void Pairing_code_expires_after_ten_minutes()
    {
        using var folder = new TempSecretFolder();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var pairing = CreatePairing(CreateService(folder), time);

        var code = pairing.CreateCode();
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Null(pairing.TryRedeem(code.Code));
    }

    [Fact]
    public void Pairing_code_still_works_just_before_it_expires()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var pairing = CreatePairing(secrets, time);

        var code = pairing.CreateCode();
        time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(secrets.Secret, pairing.TryRedeem(code.Code));
    }

    [Fact]
    public void Wrong_pairing_code_is_rejected_and_without_a_pending_code_everything_is_rejected()
    {
        using var folder = new TempSecretFolder();
        var pairing = CreatePairing(CreateService(folder));

        Assert.Null(pairing.TryRedeem("12345678"));

        var code = pairing.CreateCode().Code;
        var wrong = code == "00000000" ? "11111111" : "00000000";
        Assert.Null(pairing.TryRedeem(wrong));
        Assert.Null(pairing.TryRedeem(null));
        Assert.Null(pairing.TryRedeem(""));
    }

    [Fact]
    public void Pairing_code_is_discarded_after_too_many_wrong_attempts()
    {
        using var folder = new TempSecretFolder();
        var pairing = CreatePairing(CreateService(folder));

        var code = pairing.CreateCode().Code;
        var wrong = code == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < WorkerPairingService.MaxFailedAttempts; i++)
            Assert.Null(pairing.TryRedeem(wrong));

        Assert.Null(pairing.TryRedeem(code));
    }

    [Fact]
    public void New_pairing_code_replaces_the_previous_one()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);
        var pairing = CreatePairing(secrets);

        var first = pairing.CreateCode().Code;
        string second;
        do
        {
            second = pairing.CreateCode().Code;
        }
        while (second == first);

        Assert.Null(pairing.TryRedeem(first));
        Assert.Equal(secrets.Secret, pairing.TryRedeem(second));
    }

    // --- Pair endpoint and install script ------------------------------------------

    [Fact]
    public void Pair_endpoint_returns_the_secret_for_a_valid_code_and_401_otherwise()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);
        var pairing = CreatePairing(secrets);

        var rejected = WorkerEndpoints.PairWorker(new WorkerEndpoints.WorkerPairRequest("00000000"), pairing);
        Assert.IsType<UnauthorizedHttpResult>(rejected);

        var code = pairing.CreateCode().Code;
        var accepted = WorkerEndpoints.PairWorker(new WorkerEndpoints.WorkerPairRequest(code), pairing);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(accepted).Value;
        Assert.Equal(secrets.Secret, Assert.IsType<WorkerEndpoints.WorkerPairResponse>(value).Secret);

        var missingBody = WorkerEndpoints.PairWorker(null, pairing);
        Assert.IsType<UnauthorizedHttpResult>(missingBody);
    }

    private static string ReadInstallScriptTemplate()
    {
        using var stream = typeof(WorkerEndpoints).Assembly.GetManifestResourceStream("GrayMoon.App.Resources.install-worker.ps1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Install_script_never_contains_the_secret_or_a_leftover_placeholder()
    {
        using var folder = new TempSecretFolder();
        var secrets = CreateService(folder);

        var script = WorkerEndpoints.RenderInstallScript(ReadInstallScriptTemplate(), "http://localhost:8384", secretRequired: false);

        Assert.DoesNotContain(secrets.Secret, script, StringComparison.Ordinal);
        Assert.DoesNotContain("{SECRET_REQUIRED}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{BASE_URL}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{HUB_URL}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{DOWNLOAD_URL}", script, StringComparison.Ordinal);
        Assert.Contains("$baseUrl     = 'http://localhost:8384'", script, StringComparison.Ordinal);
        Assert.Contains("'0' -eq '1'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_script_says_a_secret_is_required_once_that_is_the_rule()
    {
        var script = WorkerEndpoints.RenderInstallScript(ReadInstallScriptTemplate(), "http://localhost:8384", secretRequired: true);

        Assert.Contains("'1' -eq '1'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_script_pairs_through_the_pair_endpoint_and_stops_when_pairing_fails()
    {
        var template = ReadInstallScriptTemplate();

        Assert.Contains("/api/worker/pair", template, StringComparison.Ordinal);
        Assert.Contains("Open GrayMoon > Worker and copy a new pairing code.", template, StringComparison.Ordinal);
        Assert.Contains("worker.secret", template, StringComparison.Ordinal);
    }
}
