using GrayMoon.App.Services.Orchestration;

namespace GrayMoon.App.Tests;

public sealed class TwoLaneProgressTests
{
    [Fact]
    public void Shows_whichever_lane_reported_last()
    {
        var published = new List<string>();
        var progress = new TwoLaneProgress(published.Add);

        progress.Update("Committing...\nLevel 2");
        progress.Push("Pushing...\nLevel 1");
        progress.Update("Updating 2 repositories...\nLevel 2");

        Assert.Equal(new[] { "Committing...\nLevel 2", "Pushing...\nLevel 1", "Updating 2 repositories...\nLevel 2" }, published);
    }

    [Fact]
    public void Ending_a_lane_shows_the_other_lanes_latest_message()
    {
        var published = new List<string>();
        var progress = new TwoLaneProgress(published.Add);
        progress.Update("Updating...\nLevel 3");
        progress.Push("Waiting for packages...\nLevel 2");
        published.Clear();

        progress.EndUpdate();
        progress.EndPush();

        Assert.Equal(new[] { "Waiting for packages...\nLevel 2" }, published);
    }

    [Fact]
    public void Ending_the_only_lane_publishes_nothing()
    {
        var published = new List<string>();
        var progress = new TwoLaneProgress(published.Add);

        progress.EndUpdate();
        progress.EndPush();

        Assert.Empty(published);
    }

    [Fact]
    public void Concurrent_reports_are_published_one_at_a_time()
    {
        var inCallback = 0;
        var overlapped = false;
        var progress = new TwoLaneProgress(_ =>
        {
            if (Interlocked.Increment(ref inCallback) > 1) overlapped = true;
            Thread.SpinWait(50);
            Interlocked.Decrement(ref inCallback);
        });

        Parallel.For(0, 2_000, i =>
        {
            if (i % 2 == 0) progress.Update($"u{i}");
            else progress.Push($"p{i}");
        });

        Assert.False(overlapped);
    }
}
