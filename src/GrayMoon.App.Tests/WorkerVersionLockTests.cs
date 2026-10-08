using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// The App and the Worker are a matched pair: a different Worker version blocks normal commands everywhere, while the
/// Worker update and diagnostics keep working whatever version the Worker reports.
/// </summary>
public sealed class WorkerVersionLockTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.3-feedback.5+sha.1234", "1.2.3-feedback.5")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_drops_whitespace_prefix_and_build_metadata(string? version, string? expected) =>
        Assert.Equal(expected, WorkerVersionPolicy.Normalize(version));

    [Theory]
    [InlineData("0.2.0", "0.2.0", true)]
    [InlineData("0.2.0+abcdef", "0.2.0+ghijkl", true)]
    [InlineData("v0.2.0", "0.2.0", true)]
    [InlineData("0.2.0-Feedback.5", "0.2.0-feedback.5", true)]
    [InlineData("0.2.0-feedback.5", "0.2.0-feedback.6", false)]
    [InlineData("0.2.0", "0.2.1", false)]
    [InlineData("0.2.0", null, false)]
    [InlineData(null, null, false)]
    public void Same_product_version_ignores_only_build_metadata(string? a, string? b, bool expected) =>
        Assert.Equal(expected, WorkerVersionPolicy.AreSameProductVersion(a, b));

    [Fact]
    public void Only_the_worker_update_and_diagnostics_run_on_a_version_mismatch()
    {
        Assert.True(WorkerVersionPolicy.IsAllowedOnVersionMismatch(WorkerHubMethods.SelfUpdate));
        Assert.True(WorkerVersionPolicy.IsAllowedOnVersionMismatch(WorkerHubMethods.GetHostInfo));

        Assert.False(WorkerVersionPolicy.IsAllowedOnVersionMismatch(WorkerHubMethods.AttachWorkspaceRepository));
        Assert.False(WorkerVersionPolicy.IsAllowedOnVersionMismatch("SyncRepository"));
        Assert.False(WorkerVersionPolicy.IsAllowedOnVersionMismatch("GetCapabilities"));
    }

    [Fact]
    public void Tracker_is_online_for_a_matching_version_and_mismatched_otherwise()
    {
        var tracker = new WorkerConnectionTracker("2.0.0");
        tracker.OnWorkerConnected("w");

        tracker.ReportWorkerSemVer("w", "2.0.0+build.7");
        Assert.Equal(WorkerConnectionState.Online, tracker.State);

        tracker.ReportWorkerSemVer("w", "1.9.0");
        Assert.Equal(WorkerConnectionState.VersionMismatch, tracker.State);

        // A matching Worker reconnecting returns to Online.
        tracker.OnWorkerDisconnected("w");
        tracker.OnWorkerConnected("w2");
        tracker.ReportWorkerSemVer("w2", "2.0.0");
        Assert.Equal(WorkerConnectionState.Online, tracker.State);
        Assert.Equal("2.0.0", tracker.AppSemVer);
    }

    [Fact]
    public void Self_update_finishes_on_the_same_product_version_even_with_different_build_metadata()
    {
        var tracker = new WorkerConnectionTracker("2.0.0+app");
        tracker.OnWorkerConnected("old");
        tracker.ReportWorkerSemVer("old", "1.0.0");
        tracker.BeginSelfUpdate();

        tracker.OnWorkerDisconnected("old");
        tracker.OnWorkerConnected("new");
        tracker.ReportWorkerSemVer("new", "2.0.0+worker");

        Assert.Equal(WorkerConnectionState.Online, tracker.State);
        Assert.False(tracker.IsSelfUpdateInProgress);
    }

    [Fact]
    public async Task Bridge_refuses_normal_commands_on_a_version_mismatch()
    {
        var (bridge, hub, tracker) = CreateBridge(appVersion: "2.0.0", workerVersion: "1.0.0");
        Assert.Equal(WorkerConnectionState.VersionMismatch, tracker.State);

        var response = await bridge.SendCommandAsync(WorkerHubMethods.AttachWorkspaceRepository, new { });

        Assert.False(response.Success);
        Assert.Equal(
            "The GrayMoon Worker version (1.0.0) does not match this GrayMoon version (2.0.0). Update the Worker.",
            response.Error);
        Assert.Empty(hub.Commands);
        Assert.Equal(response.Error, bridge.GetUnavailableReason());
        Assert.True(bridge.IsWorkerConnected);
    }

    [Fact]
    public async Task Bridge_still_sends_the_worker_update_and_diagnostics_on_a_version_mismatch()
    {
        var (bridge, hub, _) = CreateBridge(appVersion: "2.0.0", workerVersion: "1.0.0");

        var hostInfo = await bridge.SendCommandAsync(WorkerHubMethods.GetHostInfo, new { });
        var update = await bridge.SendCommandAsync(WorkerHubMethods.SelfUpdate, new { installUrl = "https://example.invalid/install" });

        Assert.True(hostInfo.Success, hostInfo.Error);
        Assert.True(update.Success, update.Error);
        Assert.Equal([WorkerHubMethods.GetHostInfo, WorkerHubMethods.SelfUpdate], hub.Commands.ToArray());
    }

    [Fact]
    public async Task Bridge_sends_normal_commands_when_versions_match()
    {
        var (bridge, hub, _) = CreateBridge(appVersion: "2.0.0", workerVersion: "2.0.0");

        var response = await bridge.SendCommandAsync("SyncRepository", new { });

        Assert.True(response.Success, response.Error);
        Assert.Equal(["SyncRepository"], hub.Commands.ToArray());
        Assert.Null(bridge.GetUnavailableReason());
    }

    [Fact]
    public void Bridge_reports_a_missing_worker()
    {
        var hub = new RecordingHubContext();
        var tracker = new WorkerConnectionTracker("2.0.0");
        var bridge = new WorkerBridge(
            hub,
            tracker,
            new WorkerCommandCancelSender(hub, tracker, NullLogger<WorkerCommandCancelSender>.Instance),
            Options.Create(new WorkerBridgeOptions()),
            NullLogger<WorkerBridge>.Instance);

        Assert.Equal(WorkerBridge.NotConnectedMessage, bridge.GetUnavailableReason());
    }

    [Fact]
    public void Workspace_repository_capability_plumbing_is_gone()
    {
        var appTypes = typeof(WorkerBridge).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("WorkerFeatureSupportService", appTypes);
        Assert.DoesNotContain("IWorkerFeatureSupportService", appTypes);
        Assert.DoesNotContain("GetCapabilitiesWorkerResponse", appTypes);
        Assert.DoesNotContain(
            typeof(WorkerHubMethods).Assembly.GetTypes(),
            t => t.Name == "WorkerFeatures");

        // The Repositories page no longer carries a Workspace-specific Worker compatibility banner.
        var members = typeof(WorkspaceRepositories)
            .GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(m => m.Name)
            .ToHashSet();
        Assert.DoesNotContain("ShowWorkerCompatibilityBanner", members);
        Assert.DoesNotContain("_workerSupportsWorkspaceRepository", members);
        Assert.DoesNotContain("RefreshWorkerSupportInBackgroundAsync", members);
    }

    private static (WorkerBridge Bridge, RecordingHubContext Hub, WorkerConnectionTracker Tracker) CreateBridge(string appVersion, string workerVersion)
    {
        var hub = new RecordingHubContext();
        var tracker = new WorkerConnectionTracker(appVersion);
        tracker.OnWorkerConnected("worker-1");
        tracker.ReportWorkerSemVer("worker-1", workerVersion);
        var bridge = new WorkerBridge(
            hub,
            tracker,
            new WorkerCommandCancelSender(hub, tracker, NullLogger<WorkerCommandCancelSender>.Instance),
            Options.Create(new WorkerBridgeOptions()),
            NullLogger<WorkerBridge>.Instance);
        return (bridge, hub, tracker);
    }

    /// <summary>Hub context whose Worker answers every RequestCommand immediately with success and records the command.</summary>
    private sealed class RecordingHubContext : IHubContext<WorkerHub>
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
