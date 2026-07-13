using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public class WorkspaceGitChangesActivityTrackerTests
{
    private static WorkspaceGitChangesActivityTracker CreateTracker(int graceMinutes = 8) =>
        new(Options.Create(new GitChangesOptions { WorkspaceActivityGraceMinutes = graceMinutes }));

    [Fact]
    public void Never_subscribed_workspace_is_not_active()
    {
        var tracker = CreateTracker();

        Assert.False(tracker.IsActive(1));
        Assert.Empty(tracker.GetActiveWorkspaceIds());
    }

    [Fact]
    public void Subscribing_makes_a_workspace_active_and_included_in_the_active_set()
    {
        var tracker = CreateTracker();

        using var lease = tracker.Subscribe(1);

        Assert.True(tracker.IsActive(1));
        Assert.Contains(1, tracker.GetActiveWorkspaceIds());
    }

    [Fact]
    public void Ref_counting_keeps_a_workspace_active_while_any_lease_remains()
    {
        var tracker = CreateTracker();

        var leaseA = tracker.Subscribe(1);
        var leaseB = tracker.Subscribe(1);

        leaseA.Dispose();

        // One of two leases released - the second tab/circuit is still viewing this workspace.
        Assert.True(tracker.IsActive(1));

        leaseB.Dispose();

        // Both released, but still within the grace window - must not immediately deactivate, since a
        // quick navigate-away-and-back (or a second tab opening moments later) should not be treated as
        // a fresh cold start requiring another warm-up scan.
        Assert.True(tracker.IsActive(1));
    }

    [Fact]
    public void Disposing_the_same_lease_twice_does_not_double_release()
    {
        var tracker = CreateTracker();

        var leaseA = tracker.Subscribe(1);
        var leaseB = tracker.Subscribe(1);

        leaseA.Dispose();
        leaseA.Dispose(); // must be a no-op, not a second decrement

        Assert.True(tracker.IsActive(1));

        leaseB.Dispose();

        Assert.True(tracker.IsActive(1)); // still within grace
    }

    [Fact]
    public void Different_workspaces_are_tracked_independently()
    {
        var tracker = CreateTracker();

        using var lease = tracker.Subscribe(1);

        Assert.True(tracker.IsActive(1));
        Assert.False(tracker.IsActive(2));

        var active = tracker.GetActiveWorkspaceIds();
        Assert.Contains(1, active);
        Assert.DoesNotContain(2, active);
    }

    [Fact]
    public void Zero_or_negative_configured_grace_is_clamped_to_at_least_one_minute()
    {
        // A workspace that just lost its last subscriber must still read as active immediately after -
        // a misconfigured near-zero grace must not defeat that, which would make every navigation look
        // like a cold start and defeat the point of activity-scoped monitoring.
        var tracker = CreateTracker(graceMinutes: 0);

        var lease = tracker.Subscribe(1);
        lease.Dispose();

        Assert.True(tracker.IsActive(1));
    }
}
