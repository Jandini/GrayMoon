using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

public sealed class FeatureRemovalDepartureTests
{
    [Fact]
    public async Task Confirm_queues_then_persists_workspace_and_strips_context()
    {
        var feature = new WorkspaceFeatureContextId(42);
        var queue = new FakeQueue();
        var selected = new FakeSelected();
        var service = new FeatureRemovalDeparture(queue, new FakeResolver(), selected);

        var leave = await service.ConfirmAsync(
            feature,
            new RemoveFeatureOptions(),
            "http://localhost/workspaces/19/repositories?context=42&q=repo:api");

        Assert.True(leave.Started);
        Assert.Null(leave.Error);
        Assert.Equal(33, leave.Workspace!.ContextId.Value);
        Assert.True(leave.Workspace.IsSpecialWorkspace);
        Assert.Equal([feature], queue.Enqueued);
        Assert.Equal([(19, 33)], selected.SetCalls);
        Assert.Equal("http://localhost/workspaces/19/repositories?q=repo:api", Uri.UnescapeDataString(leave.Path));
    }

    [Fact]
    public async Task Confirm_leaves_for_workspace_when_feature_needs_repair()
    {
        var feature = new WorkspaceFeatureContextId(42);
        var resolver = new FakeResolver { FeatureLifecycle = "NeedsRepair" };
        var selected = new FakeSelected();
        var service = new FeatureRemovalDeparture(new FakeQueue(), resolver, selected);

        var leave = await service.ConfirmAsync(
            feature,
            new RemoveFeatureOptions(),
            "http://localhost/workspaces/19/repositories?context=42");

        Assert.True(leave.Started);
        Assert.Equal("NeedsRepair", resolver.ResolvedFeatureLifecycle);
        Assert.Equal(33, leave.Workspace!.ContextId.Value);
        Assert.Equal("http://localhost/workspaces/19/repositories", leave.Path);
        Assert.Equal([(19, 33)], selected.SetCalls);
    }

    [Fact]
    public async Task Confirm_when_already_on_workspace_still_persists_and_returns_the_workspace_path()
    {
        var selected = new FakeSelected();
        var service = new FeatureRemovalDeparture(new FakeQueue(), new FakeResolver(), selected);

        var leave = await service.ConfirmAsync(
            new WorkspaceFeatureContextId(42),
            new RemoveFeatureOptions(),
            "http://localhost/workspaces/19/repositories");

        Assert.True(leave.Started);
        Assert.Equal([(19, 33)], selected.SetCalls);
        Assert.Equal("http://localhost/workspaces/19/repositories", leave.Path);
    }

    [Fact]
    public async Task Confirm_already_queued_does_not_change_selection_or_path()
    {
        var selected = new FakeSelected();
        var service = new FeatureRemovalDeparture(
            new FakeQueue { EnqueueResult = false },
            new FakeResolver(),
            selected);

        var leave = await service.ConfirmAsync(
            new WorkspaceFeatureContextId(42),
            new RemoveFeatureOptions(),
            "http://localhost/workspaces/19/repositories?context=42");

        Assert.False(leave.Started);
        Assert.Equal("This Feature is already being removed.", leave.Error);
        Assert.Null(leave.Workspace);
        Assert.Empty(leave.Path);
        Assert.Empty(selected.SetCalls);
    }

    [Fact]
    public async Task Confirm_refuses_the_special_workspace()
    {
        var selected = new FakeSelected();
        var queue = new FakeQueue();
        var service = new FeatureRemovalDeparture(queue, new FakeResolver(), selected);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmAsync(
                new WorkspaceFeatureContextId(33),
                new RemoveFeatureOptions(),
                "http://localhost/workspaces/19/repositories"));

        Assert.Equal("Cannot remove the special Workspace context.", error.Message);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(selected.SetCalls);
    }

    [Theory]
    [InlineData("http://localhost/workspaces/19/repositories?context=42", "http://localhost/workspaces/19/repositories")]
    [InlineData("http://localhost/workspaces/19/repositories?context=42&q=repo:api", "http://localhost/workspaces/19/repositories?q=repo:api")]
    [InlineData("http://localhost/workspaces/19/repositories", "http://localhost/workspaces/19/repositories")]
    [InlineData("http://localhost/workspaces/19/changes?q=repo:api&context=7", "http://localhost/workspaces/19/changes?q=repo:api")]
    public void WorkspacePath_strips_context_and_keeps_other_query(string currentUri, string expected)
    {
        Assert.Equal(expected, Uri.UnescapeDataString(FeatureRemovalDeparture.WorkspacePath(currentUri)));
    }

    private sealed class FakeQueue : IFeatureRemovalQueue
    {
        public bool EnqueueResult { get; set; } = true;
        public List<WorkspaceFeatureContextId> Enqueued { get; } = [];

        public event Action<FeatureRemovalCompleted>? Completed
        {
            add { }
            remove { }
        }

        public bool IsPending(WorkspaceFeatureContextId featureContextId) => false;

        public Task<bool> TryEnqueueAsync(
            WorkspaceFeatureContextId featureContextId,
            RemoveFeatureOptions options,
            CancellationToken cancellationToken = default)
        {
            Enqueued.Add(featureContextId);
            return Task.FromResult(EnqueueResult);
        }
    }

    private sealed class FakeResolver : IWorkspaceFeatureContextResolver
    {
        public string? FeatureLifecycle { get; init; }
        public string? ResolvedFeatureLifecycle { get; private set; }

        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(
            int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceFeatureContextId(33));

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(
            WorkspaceFeatureContextId contextId,
            int? expectedWorkspaceId = null,
            CancellationToken cancellationToken = default)
        {
            var isSpecial = contextId.Value == 33;
            if (!isSpecial)
                ResolvedFeatureLifecycle = FeatureLifecycle;
            return Task.FromResult(new WorkspaceFeatureContextInfo
            {
                ContextId = contextId,
                WorkspaceId = 19,
                IsSpecialWorkspace = isSpecial,
                FeatureName = isSpecial ? null : "broken-feature",
                LifecycleState = isSpecial ? null : FeatureLifecycle
            });
        }

        public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(
            int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFeatureContextInfo>>([]);
    }

    private sealed class FakeSelected : IWorkspaceSelectedFeatureContextService
    {
        public List<(int WorkspaceId, int ContextId)> SetCalls { get; } = [];

        public Task<WorkspaceFeatureContextId?> GetSelectedAsync(
            int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkspaceFeatureContextId?>(null);

        public Task SetSelectedAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
        {
            SetCalls.Add((workspaceId, contextId.Value));
            return Task.CompletedTask;
        }
    }
}
