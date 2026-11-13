using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceCodeGraphActivationTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public void Absent_unchecked_switch_is_not_written(bool? stored, bool selected, bool expected)
    {
        Assert.Equal(expected, WorkspaceCodeGraphActivation.ShouldWrite(stored, selected));
    }

    [Theory]
    [InlineData("Started", true)]
    [InlineData("AlreadyRunning", true)]
    [InlineData("AlreadyInitialized", false)]
    [InlineData("NotInstalled", false)]
    public void Only_a_running_build_is_watched(string outcome, bool expected)
    {
        Assert.Equal(expected, FeatureCodeGraphService.ShouldWatch(outcome));
    }

    [Fact]
    public async Task Already_initialized_workspace_does_not_watch()
    {
        var bridge = Bridge("AlreadyInitialized");
        var operations = new RecordingLock();
        var activation = Create(bridge, operations);

        Assert.Null(await activation.StartIndexAsync(7, "/workspaces/7"));

        Assert.Equal(WorkerHubMethods.InitCodeGraph, Assert.Single(bridge.Sent).Command);
        Assert.Equal(@"C:\work\ws", ManifestTestFixture.ToJson(bridge.Sent[0].Args).GetProperty("targetRoot").GetString());
        Assert.Empty(operations.Kinds);
    }

    [Fact]
    public async Task Started_workspace_index_is_watched()
    {
        var bridge = Bridge(command => command == WorkerHubMethods.WatchCodeGraphInit ? "NotRunning" : "Started");
        var operations = new RecordingLock();
        var activation = Create(bridge, operations);

        Assert.Null(await activation.StartIndexAsync(7, "/workspaces/7"));

        Assert.Equal(
            [WorkerHubMethods.InitCodeGraph, WorkerHubMethods.WatchCodeGraphInit],
            bridge.Sent.Select(sent => sent.Command).ToArray());
        Assert.Equal([WorkspaceCodeGraphActivation.OperationKind], operations.Kinds);
        Assert.Equal(["/workspaces/7"], operations.OverlayKeys);
    }

    [Fact]
    public async Task Not_installed_is_reported_and_not_watched()
    {
        var bridge = Bridge("NotInstalled");
        var operations = new RecordingLock();
        var activation = Create(bridge, operations);

        Assert.Equal("CodeGraph is not installed.", await activation.StartIndexAsync(7, "/workspaces"));
        Assert.Empty(operations.Kinds);
    }

    [Fact]
    public async Task The_watched_workspace_is_named_while_its_index_builds_and_not_afterwards()
    {
        // The Workspaces list overlay finds the operation through this. It used to look at the Workspace the
        // settings dialog last opened, so an index started by Add Workspace had no overlay at all.
        var bridge = Bridge(command => command == WorkerHubMethods.WatchCodeGraphInit ? "NotRunning" : "Started");
        var operations = new RecordingLock();
        var activation = Create(bridge, operations);
        int? duringWatch = null;
        operations.BeforeWork = () => duringWatch = activation.ActiveWorkspaceId;

        Assert.Null(activation.ActiveWorkspaceId);
        Assert.Null(await activation.StartIndexAsync(7, "/workspaces"));

        Assert.Equal(7, duringWatch);
        Assert.Null(activation.ActiveWorkspaceId);
    }

    [Fact]
    public async Task An_index_that_is_already_there_names_no_workspace()
    {
        var activation = Create(Bridge("AlreadyInitialized"), new RecordingLock());

        Assert.Null(await activation.StartIndexAsync(7, "/workspaces"));

        Assert.Null(activation.ActiveWorkspaceId);
    }

    [Fact]
    public async Task A_build_that_started_and_failed_is_not_reported_as_not_started()
    {
        var bridge = Bridge(command => command == WorkerHubMethods.WatchCodeGraphInit ? "Failed" : "Started");
        var activation = Create(bridge, new RecordingLock());

        Assert.Equal("The CodeGraph index build failed.", await activation.StartIndexAsync(7, "/workspaces/7"));
    }

    [Theory]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, 3, false)]
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    public void Add_workspace_refuses_a_ticked_box_with_no_workspace_repository_before_it_creates_anything(
        bool offered, bool selected, int? workspaceRepositoryId, bool refused)
    {
        var message = WorkspaceCodeGraphActivation.MissingWorkspaceRepositoryMessage(offered, selected, workspaceRepositoryId);

        Assert.Equal(refused ? WorkspaceManifestService.CodeGraphNeedsWorkspaceRepositoryMessage : null, message);
    }

    [Fact]
    public async Task Settings_opened_from_a_feature_index_that_feature_folder_not_the_workspace_folder()
    {
        // The save used to index and watch the special Workspace folder whichever context the dialog came from.
        var bridge = Bridge(command => command == WorkerHubMethods.WatchCodeGraphInit ? "Completed" : "Started");
        var activation = Create(bridge, new RecordingLock(), codeGraphEnabled: false);

        Assert.Null(await activation.StartIndexAsync(7, "/workspaces/7", new WorkspaceFeatureContextId(42)));

        var sent = bridge.Sent.Select(s => (s.Command, Args: ManifestTestFixture.ToJson(s.Args).GetRawText())).ToList();
        Assert.Contains(sent, s => s.Command == WorkerHubMethods.InitCodeGraph && s.Args.Contains(@"C:\\features\\f42", StringComparison.Ordinal));
        // The special Workspace's switch is off here; the Feature's own build is still watched.
        Assert.Contains(sent, s => s.Command == WorkerHubMethods.WatchCodeGraphInit && s.Args.Contains(@"C:\\features\\f42", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, s => s.Args.Contains(@"C:\\work\\ws", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_opened_from_the_workspaces_list_index_the_workspace_folder()
    {
        var bridge = Bridge(command => command == WorkerHubMethods.WatchCodeGraphInit ? "Completed" : "Started");
        var activation = Create(bridge, new RecordingLock());

        Assert.Null(await activation.StartIndexAsync(7, "/workspaces"));

        Assert.All(bridge.Sent, s => Assert.Contains(@"C:\\work\\ws", ManifestTestFixture.ToJson(s.Args).GetRawText(), StringComparison.Ordinal));
    }

    private static WorkspaceCodeGraphActivation Create(ScriptedWorkerBridge bridge, RecordingLock operations, bool codeGraphEnabled = true)
    {
        var manifest = new RecordingManifestService { CodeGraphEnabled = codeGraphEnabled };
        var codeGraph = new FeatureCodeGraphService(
            manifest,
            bridge,
            new WorkspaceRootResolver(),
            new FakeFeatureContextResolver(),
            NullLogger<FeatureCodeGraphService>.Instance);
        return new WorkspaceCodeGraphActivation(manifest, codeGraph, operations, NullLogger<WorkspaceCodeGraphActivation>.Instance);
    }

    private static ScriptedWorkerBridge Bridge(string outcome) => Bridge(_ => outcome);

    private static ScriptedWorkerBridge Bridge(Func<string, string> outcome) => new()
    {
        Handler = (command, _) => new WorkerCommandResponse(true, new { outcome = outcome(command), next = 0 }, null),
    };

    private sealed class WorkspaceRootResolver : IWorkspaceContextPathResolver
    {
        public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default) =>
            Task.FromResult(contextId.Value == 1 ? @"C:\work\ws" : @"C:\features\f" + contextId.Value);

        public Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLock : IWorkspaceOperationLock
    {
        public List<string> Kinds { get; } = [];

        public List<string> OverlayKeys { get; } = [];

        public Action? BeforeWork { get; set; }

        public bool IsWorkspaceStructurallyBusy(int workspaceId) => false;

        public bool IsContextBusy(WorkspaceFeatureContextId contextId) => false;

        public bool TryStartContext(
            WorkspaceFeatureContextId contextId,
            int workspaceId,
            string operationKind,
            string overlayKey,
            string displayMessage,
            Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
            out IWorkspaceLockedOperation operation)
        {
            operation = new ImmediateOperation();
            return false;
        }

        public bool TryStartStructural(
            int workspaceId,
            string operationKind,
            string overlayKey,
            string displayMessage,
            Func<IWorkspaceLockedOperation, CancellationToken, Task> work,
            out IWorkspaceLockedOperation operation)
        {
            Kinds.Add(operationKind);
            OverlayKeys.Add(overlayKey);
            BeforeWork?.Invoke();
            operation = new ImmediateOperation();
            work(operation, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
    }

    private sealed class ImmediateOperation : IWorkspaceLockedOperation
    {
        public int WorkspaceId => 7;
        public WorkspaceFeatureContextId? ContextId => null;
        public string OperationKind => WorkspaceCodeGraphActivation.OperationKind;
        public string OverlayKey => "/workspaces/7";
        public string DisplayMessage => "Building the CodeGraph index...";
        public Task WhenCompleted => Task.CompletedTask;
        public CancellationToken CancellationToken => CancellationToken.None;
        public void ReportProgress(string message) { }
        public Task RunBackgroundableAsync(Func<CancellationToken, Task> work) => work(CancellationToken.None);
    }
}
