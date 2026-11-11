using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Worker;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>A Worker that reports a damaged install is refused once with a reinstall message, not command by command.</summary>
public sealed class WorkerInstallDamageTests
{
    [Fact]
    public async Task A_damaged_install_refuses_normal_commands_but_still_allows_the_update_and_diagnostics()
    {
        var (bridge, hub, tracker) = CreateBridge();
        tracker.ReportInstallDamage("worker-1", "DllNotFoundException: git2-5853918");

        var refused = await bridge.SendCommandAsync("SyncRepository", new { });
        var hostInfo = await bridge.SendCommandAsync(WorkerHubMethods.GetHostInfo, new { });
        var update = await bridge.SendCommandAsync(WorkerHubMethods.SelfUpdate, new { installUrl = "https://example.invalid/install" });

        Assert.False(refused.Success);
        Assert.Contains("install is damaged", refused.Error);
        Assert.Contains("git2-5853918", refused.Error);
        Assert.True(hostInfo.Success, hostInfo.Error);
        Assert.True(update.Success, update.Error);
        Assert.Equal([WorkerHubMethods.GetHostInfo, WorkerHubMethods.SelfUpdate], hub.Commands.ToArray());
        Assert.Contains("install is damaged", bridge.GetUnavailableReason());
    }

    [Fact]
    public async Task A_worker_that_reports_no_damage_runs_normal_commands()
    {
        var (bridge, hub, tracker) = CreateBridge();

        var response = await bridge.SendCommandAsync("SyncRepository", new { });

        Assert.True(response.Success, response.Error);
        Assert.Null(tracker.InstallDamage);
        Assert.Null(bridge.GetUnavailableReason());
        Assert.Equal(["SyncRepository"], hub.Commands.ToArray());
    }

    [Fact]
    public void The_damage_ends_with_the_connection_that_reported_it()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("old");
        tracker.ReportInstallDamage("old", "broken");
        Assert.Equal("broken", tracker.InstallDamage);

        tracker.OnWorkerConnected("new");
        Assert.Null(tracker.InstallDamage);

        tracker.OnWorkerDisconnected("new");
        Assert.Equal("broken", tracker.InstallDamage);

        tracker.OnWorkerDisconnected("old");
        Assert.Null(tracker.InstallDamage);
    }

    [Fact]
    public void Reporting_the_damage_notifies_listeners_once()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("w");
        var notifications = 0;
        tracker.OnStateChanged(_ => notifications++);
        notifications = 0;

        tracker.ReportInstallDamage("w", "broken");
        tracker.ReportInstallDamage("w", "broken");

        Assert.Equal(1, notifications);
    }

    private static (WorkerBridge Bridge, RecordingHub Hub, WorkerConnectionTracker Tracker) CreateBridge()
    {
        var hub = new RecordingHub();
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("worker-1");
        tracker.ReportWorkerSemVer("worker-1", "2.0.0");
        var bridge = new WorkerBridge(
            hub,
            tracker,
            new WorkerCommandCancelSender(hub, tracker, NullLogger<WorkerCommandCancelSender>.Instance),
            Options.Create(new WorkerBridgeOptions()),
            NullLogger<WorkerBridge>.Instance);
        return (bridge, hub, tracker);
    }

    /// <summary>Hub context whose Worker answers every RequestCommand immediately with success and records the command.</summary>
    private sealed class RecordingHub : IHubContext<WorkerHub>
    {
        private readonly RecordingClients _clients = new();

        public List<string> Commands => _clients.Commands;

        public IHubClients Clients => _clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients : IHubClients, IClientProxy
    {
        public List<string> Commands { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (method == WorkerHubMethods.RequestCommand)
            {
                var requestId = (string)args[0]!;
                Commands.Add((string)args[1]!);
                WorkerResponseDelivery.Complete(requestId, new WorkerCommandResponse(true, null, null));
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
