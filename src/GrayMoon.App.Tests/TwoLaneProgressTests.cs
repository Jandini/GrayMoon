using GrayMoon.App.Services.Orchestration;

namespace GrayMoon.App.Tests;

public sealed class TwoLaneProgressTests
{
    [Fact]
    public void Join_returns_the_single_lane_unchanged()
    {
        Assert.Equal("Updating 2 repositories...\nLevel 1", OverlayLanes.Join("Updating 2 repositories...\nLevel 1", null));
        Assert.Equal("Pushing...\nLevel 1", OverlayLanes.Join("  ", "Pushing...\nLevel 1"));
        Assert.Equal(string.Empty, OverlayLanes.Join(null, null));
    }

    [Fact]
    public void Join_and_Split_round_trip_two_lanes_in_order()
    {
        var joined = OverlayLanes.Join("Committing...\nLevel 3", "Waiting for packages...\nLevel 2");

        Assert.Equal(new[] { "Committing...\nLevel 3", "Waiting for packages...\nLevel 2" }, OverlayLanes.Split(joined));
    }

    [Fact]
    public void Split_treats_a_message_without_a_separator_as_one_lane_and_ignores_blanks()
    {
        Assert.Equal(new[] { "Pushing...\nLevel 1" }, OverlayLanes.Split("Pushing...\nLevel 1"));
        Assert.Empty(OverlayLanes.Split(null));
        Assert.Empty(OverlayLanes.Split(" \n"));
    }

    [Fact]
    public void Progress_publishes_both_lanes_then_only_the_remaining_one_as_lanes_end()
    {
        var published = new List<string>();
        var progress = new TwoLaneProgress(published.Add);

        progress.Update("Updating...\nLevel 1");
        progress.Push("Pushing...\nLevel 1");
        progress.EndUpdate();
        progress.EndPush();

        Assert.Equal(
            new[]
            {
                "Updating...\nLevel 1",
                OverlayLanes.Join("Updating...\nLevel 1", "Pushing...\nLevel 1"),
                "Pushing...\nLevel 1",
            },
            published);
    }

    [Fact]
    public void Concurrent_reports_never_publish_a_torn_message()
    {
        var published = new List<string>();
        var progress = new TwoLaneProgress(published.Add);

        Parallel.For(0, 2_000, i =>
        {
            if (i % 2 == 0) progress.Update($"u{i}");
            else progress.Push($"p{i}");
        });

        Assert.All(published, message => Assert.InRange(OverlayLanes.Split(message).Count, 1, 2));
    }
}
