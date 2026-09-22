using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRouteParserTests
{
    [Theory]
    [InlineData("workspaces/1", 1)]
    [InlineData("workspaces/1/projects", 1)]
    [InlineData("workspaces/1/files", 1)]
    [InlineData("workspaces/1/actions", 1)]
    [InlineData("workspaces/42/changes", 42)]
    [InlineData("workspaces/42/dependencies?context=3", 42)]
    [InlineData("Workspaces/7/packages", 7)]
    public void TryGetWorkspaceId_parses_workspace_routes(string relativeUri, int expectedId)
    {
        Assert.Equal(expectedId, WorkspaceRouteParser.TryGetWorkspaceId(relativeUri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("workspaces")]
    [InlineData("workspaces/not-a-number")]
    [InlineData("repositories/1")]
    public void TryGetWorkspaceId_returns_null_outside_workspace_routes(string? relativeUri)
    {
        Assert.Null(WorkspaceRouteParser.TryGetWorkspaceId(relativeUri));
    }
}

public sealed class WorkspaceGitChangesRouteActivityTests
{
    [Fact]
    public void Sync_to_workspace_page_other_than_repositories_or_changes_activates_workspace()
    {
        var activation = new RecordingActivation();
        using var route = new WorkspaceGitChangesRouteActivity(activation);

        route.Sync(WorkspaceRouteParser.TryGetWorkspaceId("workspaces/1/projects"));

        Assert.Equal([1], activation.ActivateCalls);
        Assert.Equal(1, route.CurrentWorkspaceId);
        Assert.True(activation.Tracker.IsActive(1));
    }

    [Fact]
    public void Navigation_within_same_workspace_does_not_reactivate()
    {
        var activation = new RecordingActivation();
        using var route = new WorkspaceGitChangesRouteActivity(activation);

        route.Sync(WorkspaceRouteParser.TryGetWorkspaceId("workspaces/1/changes"));
        route.Sync(WorkspaceRouteParser.TryGetWorkspaceId("workspaces/1/actions"));
        route.Sync(WorkspaceRouteParser.TryGetWorkspaceId("workspaces/1/files"));

        Assert.Equal([1], activation.ActivateCalls);
        Assert.Equal(1, activation.LiveLeaseCount);
    }

    [Fact]
    public void Switching_workspace_releases_previous_and_activates_next()
    {
        var activation = new RecordingActivation();
        using var route = new WorkspaceGitChangesRouteActivity(activation);

        route.Sync(1);
        route.Sync(2);

        Assert.Equal([1, 2], activation.ActivateCalls);
        Assert.Equal(2, route.CurrentWorkspaceId);
        Assert.True(activation.Tracker.IsActive(2));
        // Workspace 1 has no live lease but remains active under grace.
        Assert.True(activation.Tracker.IsActive(1));
        Assert.Equal(1, activation.LiveLeaseCount);
    }

    [Fact]
    public void Leaving_workspace_releases_direct_lease()
    {
        var activation = new RecordingActivation();
        using var route = new WorkspaceGitChangesRouteActivity(activation);

        route.Sync(1);
        route.Sync(WorkspaceRouteParser.TryGetWorkspaceId("settings"));

        Assert.Null(route.CurrentWorkspaceId);
        Assert.Equal(0, activation.LiveLeaseCount);
        Assert.True(activation.Tracker.IsActive(1));
    }

    [Fact]
    public void Two_circuits_keep_workspace_active_until_both_release()
    {
        var activation = new RecordingActivation();
        var circuitA = new WorkspaceGitChangesRouteActivity(activation);
        var circuitB = new WorkspaceGitChangesRouteActivity(activation);

        circuitA.Sync(1);
        circuitB.Sync(1);

        Assert.Equal(2, activation.LiveLeaseCount);

        circuitA.Dispose();
        Assert.Equal(1, activation.LiveLeaseCount);
        Assert.True(activation.Tracker.IsActive(1));

        circuitB.Dispose();
        Assert.Equal(0, activation.LiveLeaseCount);
        Assert.True(activation.Tracker.IsActive(1));
    }

    private sealed class RecordingActivation : IWorkspaceGitChangesActivation
    {
        public WorkspaceGitChangesActivityTracker Tracker { get; } =
            new(Options.Create(new GitChangesOptions { WorkspaceActivityGraceMinutes = 8 }));

        public List<int> ActivateCalls { get; } = [];
        public int LiveLeaseCount { get; private set; }

        public IDisposable Activate(int workspaceId)
        {
            ActivateCalls.Add(workspaceId);
            LiveLeaseCount++;
            var lease = Tracker.Subscribe(workspaceId);
            return new CountingLease(this, lease);
        }

        private void OnLeaseDisposed() => LiveLeaseCount--;

        private sealed class CountingLease(RecordingActivation owner, IDisposable inner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.OnLeaseDisposed();
                inner.Dispose();
            }
        }
    }
}
