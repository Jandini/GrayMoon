using System.Collections;
using System.Reflection;
using GrayMoon.Common;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>State the Worker kept for the life of the process without ever using or releasing it.</summary>
public sealed class WorkerRetainedStateTests : IDisposable
{
    private readonly string _root = TestDirectory.Create("retained");

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Token_provider_asks_the_App_every_time_and_keeps_no_token()
    {
        using var app = new FakeAppApi { ResponseBody = """{"connectorId":5,"token":"first"}""" };
        var provider = new WorkerTokenProvider(
            Options.Create(new WorkerOptions { AppApiBaseUrl = app.BaseUrl }),
            new FakeWorkerSecretProvider("secret"),
            NullLogger<WorkerTokenProvider>.Instance);

        var first = await provider.GetTokenForRepositoryAsync(7, CancellationToken.None);
        app.ResponseBody = """{"connectorId":5,"token":"rotated"}""";
        var second = await provider.GetTokenForRepositoryAsync(7, CancellationToken.None);
        provider.InvalidateByConnectorId(5);

        Assert.Equal("first", first);
        Assert.Equal("rotated", second);
        Assert.Equal(2, app.Requests.Count);

        // A token must not outlive the operation that asked for it: no field of the provider can hold one.
        var collections = typeof(WorkerTokenProvider)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(f.FieldType))
            .Select(f => f.Name);
        Assert.Empty(collections);
    }

    [Fact]
    public async Task CodeGraph_forgets_the_output_of_builds_whose_folder_is_gone()
    {
        var codeGraph = new CodeGraphCli(new InstantCommandLine(), new StubLifetime(), NullLogger<CodeGraphCli>.Instance);
        var removed = Directory.CreateDirectory(Path.Combine(_root, "removed-feature")).FullName;
        var kept = Directory.CreateDirectory(Path.Combine(_root, "kept-feature")).FullName;

        await BuildAsync(codeGraph, removed);
        Assert.Equal(1, codeGraph.FinishedInitCount);

        Directory.Delete(removed, recursive: true);
        await BuildAsync(codeGraph, kept);

        Assert.Equal(1, codeGraph.FinishedInitCount);
        Assert.Equal(CodeGraphCli.InitNotRunning, (await codeGraph.WatchInitAsync(removed, 0, null, CancellationToken.None)).Outcome);
        // A late watch on a folder that still exists keeps working.
        Assert.Equal(CodeGraphCli.InitCompleted, (await codeGraph.WatchInitAsync(kept, 0, null, CancellationToken.None)).Outcome);
    }

    private static async Task BuildAsync(CodeGraphCli codeGraph, string root)
    {
        Assert.True(codeGraph.TryStartInit("codegraph", root));
        var (outcome, _) = await codeGraph.WatchInitAsync(root, 0, null, CancellationToken.None);
        Assert.Equal(CodeGraphCli.InitCompleted, outcome);

        // The outcome is published just before the build leaves the running set.
        for (var i = 0; i < 200 && codeGraph.IsInitRunning(root); i++)
            await Task.Delay(10);
        Assert.False(codeGraph.IsInitRunning(root));
    }

    private sealed class InstantCommandLine : ICommandLineService
    {
        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null) =>
            throw new NotSupportedException();

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null) =>
            Task.FromResult(new CommandLineResult(0, "", ""));
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
