using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>The bridge command timeout is an inactivity bound: streamed Worker output keeps a long command alive.</summary>
public sealed class WorkerBridgeActivityTimeoutTests
{
    [Fact]
    public async Task Streamed_output_keeps_a_command_alive_past_the_timeout()
    {
        var hub = new CapturingHub();
        var bridge = CreateBridge(hub, timeoutSeconds: 1);

        var run = bridge.SendCommandAsync("SyncRepository", new { });
        var requestId = await hub.RequestIdAsync();
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(400); // 2.4 s in total, longer than the 1 s window
            WorkerResponseDelivery.ReportStreamLine(requestId, new WorkerCommandStreamLine(null, WorkerCommandStreamKind.Stdout, "Receiving objects: 1%"));
        }

        WorkerResponseDelivery.Complete(requestId, new WorkerCommandResponse(true, null, null));

        var response = await run;
        Assert.True(response.Success, response.Error);
    }

    [Fact]
    public async Task A_silent_command_times_out()
    {
        var hub = new CapturingHub();
        var bridge = CreateBridge(hub, timeoutSeconds: 1);

        var response = await bridge.SendCommandAsync("SyncRepository", new { });

        Assert.False(response.Success);
        Assert.Contains("no response or output", response.Error, StringComparison.Ordinal);
    }

    private static WorkerBridge CreateBridge(CapturingHub hub, int timeoutSeconds)
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("worker-1");
        tracker.ReportWorkerSemVer("worker-1", "2.0.0");
        return new WorkerBridge(
            hub,
            tracker,
            new WorkerCommandCancelSender(hub, tracker, NullLogger<WorkerCommandCancelSender>.Instance),
            Options.Create(new WorkerBridgeOptions { CommandTimeoutSeconds = timeoutSeconds }),
            NullLogger<WorkerBridge>.Instance);
    }

    private sealed class CapturingHub : IHubContext<WorkerHub>
    {
        private readonly CapturingClients _clients = new();

        public Task<string> RequestIdAsync() => _clients.RequestId.Task;

        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class CapturingClients : IHubClients, IClientProxy
    {
        public TaskCompletionSource<string> RequestId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (method == WorkerHubMethods.RequestCommand && args.Length > 0 && args[0] is string id)
                RequestId.TrySetResult(id);
            return Task.CompletedTask;
        }

        public IClientProxy Client(string connectionId) => this;
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }
}