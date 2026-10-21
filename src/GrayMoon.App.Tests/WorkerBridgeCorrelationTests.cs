using GrayMoon.Abstractions.Logging;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>App↔Worker hop correlation: RequestId on failed bridge responses and optional OperationId on the wire.</summary>
public sealed class WorkerBridgeCorrelationTests
{
    [Fact]
    public async Task Failed_worker_response_logs_warning_with_RequestId()
    {
        var hub = new RecordingHubContext(success: false, error: "git failed");
        var logger = new RecordingLogger<WorkerBridge>();
        var bridge = CreateBridge(hub, logger);

        var response = await bridge.SendCommandAsync("SyncRepository", new { });

        Assert.False(response.Success);
        Assert.Equal("git failed", response.Error);
        Assert.NotNull(hub.LastRequestId);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(hub.LastRequestId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("SyncRepository", warning.Message, StringComparison.Ordinal);
        Assert.Contains("git failed", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestCommand_carries_ambient_OperationId()
    {
        var hub = new RecordingHubContext(success: true);
        var bridge = CreateBridge(hub, NullLogger<WorkerBridge>.Instance);
        var operationId = GrayMoonOperationContext.NewId();

        using (GrayMoonOperationContext.Begin(operationId))
        {
            var response = await bridge.SendCommandAsync("SyncRepository", new { });
            Assert.True(response.Success, response.Error);
        }

        Assert.Equal(operationId, hub.LastOperationId);
        Assert.False(string.IsNullOrEmpty(hub.LastRequestId));
    }

    [Fact]
    public void EnsureId_reuses_ambient_OperationId()
    {
        var operationId = GrayMoonOperationContext.NewId();
        using (GrayMoonOperationContext.Begin(operationId))
        {
            Assert.Equal(operationId, GrayMoonOperationContext.EnsureId());
            Assert.Equal("explicit", GrayMoonOperationContext.EnsureId("explicit"));
        }
    }

    private static WorkerBridge CreateBridge(RecordingHubContext hub, ILogger<WorkerBridge> logger)
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("worker-1");
        tracker.ReportWorkerSemVer("worker-1", "2.0.0");
        return new WorkerBridge(
            hub,
            tracker,
            new WorkerCommandCancelSender(hub, tracker, NullLogger<WorkerCommandCancelSender>.Instance),
            Options.Create(new WorkerBridgeOptions()),
            logger);
    }

    private sealed class RecordingHubContext(bool success, string? error = null) : IHubContext<WorkerHub>
    {
        private readonly RecordingClients _clients = new(success, error);

        public string? LastRequestId => _clients.LastRequestId;
        public string? LastOperationId => _clients.LastOperationId;

        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients(bool success, string? error) : IHubClients, IClientProxy
    {
        public string? LastRequestId { get; private set; }
        public string? LastOperationId { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (method == WorkerHubMethods.RequestCommand)
            {
                LastRequestId = (string)args[0]!;
                LastOperationId = args.Length > 3 ? args[3] as string : null;
                WorkerResponseDelivery.Complete(
                    LastRequestId,
                    new WorkerCommandResponse(success, null, error));
            }

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
