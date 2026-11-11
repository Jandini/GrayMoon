using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Hosted;
using GrayMoon.Worker.Hub;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The SignalR client runs one handler at a time, so a RequestCommand that waits for room in a full queue would hold
/// back the CancelCommand meant to end it. This runs the real Worker connection against a real hub over a socket:
/// a request is sent into a full pool and cancelled right after, and the answer must still arrive.
/// </summary>
public sealed class CancelCommandDispatchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_cancel_is_dispatched_while_an_earlier_request_waits_for_room_in_a_full_pool()
    {
        var state = new HubState();
        await using var app = await StartHubAsync(state);

        var readQueue = new ReadJobQueue(Options.Create(new WorkerOptions { MaxConcurrentReadCommands = 1 }));
        var factory = new CommandJobFactory();
        const int capacity = 8; // ReadJobQueue: max(MaxConcurrentReadCommands * 2, 8); nothing reads it, as in a busy pool
        for (var i = 0; i < capacity; i++)
            await readQueue.EnqueueAsync(factory.CreateCommandJob($"filler-{i}", "GetGitChangeStatus", Json("{}")));

        var other = new NoQueue();
        var service = new SignalRConnectionHostedService(
            new HubConnectionProvider(),
            other,
            readQueue,
            other,
            other,
            factory,
            new CommandJobCancellationRegistry(),
            new FakeWorkerSecretProvider("secret"),
            Options.Create(new WorkerOptions { AppHubUrl = $"{app.Urls.First()}/hub/worker" }),
            NullLogger<SignalRConnectionHostedService>.Instance);
        await using var _ = service;
        await service.StartAsync(CancellationToken.None);
        try
        {
            var connectionId = await state.Connected.Task.WaitAsync(Wait);
            var clients = app.Services.GetRequiredService<IHubContext<FakeWorkerHub>>().Clients.Client(connectionId);

            // The request that cannot be queued, then its cancel: the second only arrives if the first returned.
            await clients.SendAsync(WorkerHubMethods.RequestCommand, "blocked", "GetGitChangeStatus", Json("{}"), (string?)null);
            await clients.SendAsync(WorkerHubMethods.CancelCommand, "blocked");

            var response = await state.Responses.Task.WaitAsync(Wait);
            Assert.Equal("blocked", response.RequestId);
            Assert.False(response.Response.Success);
            Assert.Equal("Command cancelled.", response.Response.Error);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;

    private static async Task<WebApplication> StartHubAsync(HubState state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(state);
        var app = builder.Build();
        app.MapHub<FakeWorkerHub>("/hub/worker");
        await app.StartAsync();
        return app;
    }

    public sealed class HubState
    {
        public TaskCompletionSource<string> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(string RequestId, WorkerCommandResponse Response)> Responses { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class FakeWorkerHub(HubState state) : Microsoft.AspNetCore.SignalR.Hub
    {
        public override Task OnConnectedAsync()
        {
            state.Connected.TrySetResult(Context.ConnectionId);
            return Task.CompletedTask;
        }

        public Task ReportSemVer(string semVer) => Task.CompletedTask;

        public Task ResponseCommand(string requestId, WorkerCommandResponse response)
        {
            state.Responses.TrySetResult((requestId, response));
            return Task.CompletedTask;
        }
    }

    private sealed class NoQueue : Abstractions.IJobQueue, Abstractions.IReadJobQueue, Abstractions.IDiffJobQueue, Abstractions.IAiJobQueue
    {
        public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
