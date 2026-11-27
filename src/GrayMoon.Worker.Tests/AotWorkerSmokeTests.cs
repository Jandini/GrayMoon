using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Runs only when <c>GRAYMOON_AOT_WORKER_EXE</c> names a published Native AOT Worker exe;
/// <c>dev/Test-WorkerAot.ps1</c> publishes one and sets it. Otherwise the test is reported as skipped.
/// </summary>
public sealed class AotWorkerFactAttribute : FactAttribute
{
    public const string ExeVariable = "GRAYMOON_AOT_WORKER_EXE";

    public AotWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExeVariable)))
            Skip = $"Needs a published Native AOT Worker: run dev/Test-WorkerAot.ps1 (it sets {ExeVariable}).";
    }
}

/// <summary>
/// The unit tests run on the normal runtime and so cannot show that the Native AOT exe works: there, a type missing
/// from the JSON context or a property read by name fails only when that code runs. This test starts the published
/// exe against a real hub hosted here and sends it commands the way the App does.
/// </summary>
public sealed class AotWorkerSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [AotWorkerFact]
    public async Task The_published_exe_connects_and_answers_commands()
    {
        var exe = Environment.GetEnvironmentVariable(AotWorkerFactAttribute.ExeVariable)!;
        Assert.True(File.Exists(exe), $"{AotWorkerFactAttribute.ExeVariable} points to a file that does not exist: {exe}");

        var root = TestDirectory.Create("aot-smoke");
        var state = new HubState();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 8 * 1024 * 1024);
        await using var app = builder.Build();
        app.MapHub<TestWorkerHub>("/hub/worker");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var hub = app.Services.GetRequiredService<IHubContext<TestWorkerHub>>();

        var output = new StringBuilder();
        using var worker = StartWorker(exe, address + "/hub/worker", FreePort(), output);
        try
        {
            // Version reporting: the App compares this value to decide whether the Worker must be updated.
            var semVer = await state.SemVer.Task.WaitAsync(Timeout);
            Assert.False(string.IsNullOrWhiteSpace(semVer));
            Assert.NotEqual("0.0.0", semVer);
            Assert.False(state.InstallDamage.Task.IsCompleted, "The exe reported a damaged install: " + (state.InstallDamage.Task.IsCompleted ? state.InstallDamage.Task.Result : ""));

            GitScenarios.CommittedRepository(Path.Combine(root, "ws", "repo"));

            async Task<JsonElement> Send(string command, object? args)
            {
                var requestId = Guid.NewGuid().ToString("N");
                var pending = state.Expect(requestId);
                JsonElement? json = args is null ? null : JsonSerializer.SerializeToElement(args);
                await hub.Clients.All.SendAsync(WorkerHubMethods.RequestCommand, requestId, command, json, (string?)null);
                return await pending.WaitAsync(Timeout);
            }

            // No arguments, a response with many members, and processes started by the exe.
            var hostInfo = await Send(WorkerHubMethods.GetHostInfo, null);
            Assert.True(hostInfo.GetProperty("success").GetBoolean(), hostInfo.GetRawText());
            Assert.True(hostInfo.GetProperty("data").EnumerateObject().Count() > 3, hostInfo.GetRawText());
            // The hub writes nulls: the App reads "error" on every response.
            Assert.Equal(JsonValueKind.Null, hostInfo.GetProperty("error").ValueKind);

            var exists = await Send("GetWorkspaceExists", new { workspaceRoot = root, workspaceName = "ws" });
            Assert.True(exists.GetProperty("data").GetProperty("exists").GetBoolean(), exists.GetRawText());

            // Reads the repository in process (LibGit2Sharp) and with the git CLI.
            var branches = await Send("GetBranches", new { workspaceRoot = root, workspaceName = "ws", repositoryName = "repo", repositoryId = 1 });
            Assert.True(branches.GetProperty("success").GetBoolean(), branches.GetRawText());
            var current = branches.GetProperty("data").GetProperty("currentBranch").GetString();
            Assert.False(string.IsNullOrWhiteSpace(current), branches.GetRawText());
            Assert.Contains(current, branches.GetProperty("data").GetProperty("localBranches").EnumerateArray().Select(b => b.GetString()));

            // A response that reports failure must reach the hub as failed, with its message (ICommandOutcome).
            var checkout = await Send("CheckoutBranch", new { workspaceRoot = root, workspaceName = "ws", repositoryName = "repo", branchName = "no-such-branch" });
            Assert.False(checkout.GetProperty("success").GetBoolean(), checkout.GetRawText());
            Assert.False(string.IsNullOrWhiteSpace(checkout.GetProperty("error").GetString()), checkout.GetRawText());
            Assert.False(checkout.GetProperty("data").GetProperty("success").GetBoolean(), checkout.GetRawText());

            // A command that throws: required arguments are missing.
            var missingArgs = await Send("SyncRepository", null);
            Assert.False(missingArgs.GetProperty("success").GetBoolean(), missingArgs.GetRawText());
            Assert.Contains("Args required", missingArgs.GetProperty("error").GetString());

            // Queue status crosses the hub as an int and a Dictionary<int, int>.
            await state.QueueStatus.Task.WaitAsync(Timeout);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message}{Environment.NewLine}--- Worker output ---{Environment.NewLine}{output}", ex);
        }
        finally
        {
            try { worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            worker.WaitForExit(10_000);
            TestDirectory.Delete(root);
        }
    }

    private static Process StartWorker(string exe, string hubUrl, int listenPort, StringBuilder output)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Not the exe's own folder: an installed Worker is not started from there either.
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (var argument in new[] { "run", "--hub-url", hubUrl, "--listen-port", listenPort.ToString() })
            start.ArgumentList.Add(argument);

        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public sealed class HubState
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        public TaskCompletionSource<string> SemVer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> InstallDamage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> QueueStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonElement> Expect(string requestId) =>
            _pending.GetOrAdd(requestId, _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void Complete(string requestId, JsonElement response) =>
            _pending.GetOrAdd(requestId, _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(response.Clone());
    }

    /// <summary>The Worker-facing methods of the App's hub, recording what the Worker sends.</summary>
    public sealed class TestWorkerHub(HubState state) : Microsoft.AspNetCore.SignalR.Hub
    {
        public void ReportSemVer(string semVer) => state.SemVer.TrySetResult(semVer);

        public void ReportInstallDamage(string damage) => state.InstallDamage.TrySetResult(damage);

        public void ResponseCommand(string requestId, JsonElement response) => state.Complete(requestId, response);

        public void CommandOutput(string requestId, string? label, int kind, string text) { }

        public void SyncCommand(JsonElement notification) { }

        public void ReportQueueStatus(int total, Dictionary<int, int> byWorkspace) => state.QueueStatus.TrySetResult(true);

        public void GitChangesSnapshotUpdated(JsonElement notification) { }
    }
}
