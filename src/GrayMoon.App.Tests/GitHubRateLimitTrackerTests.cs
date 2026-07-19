using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class GitHubRateLimitTrackerTests
{
    [Fact]
    public void Record_ThenGetLatest_ReturnsMostRecentSnapshotForConnector()
    {
        var tracker = new GitHubRateLimitTracker();
        var snapshot = new GitHubRateLimitSnapshot(5000, 4999, 1, 1_700_000_000);

        tracker.Record("GitHub", snapshot);

        Assert.Equal(snapshot, tracker.GetLatest("GitHub"));
    }

    [Fact]
    public void GetLatest_ReturnsNullForUnknownConnector()
    {
        var tracker = new GitHubRateLimitTracker();

        Assert.Null(tracker.GetLatest("Unknown"));
    }

    [Fact]
    public void PauseUntil_ThenGetPausedUntil_ReturnsFutureTimestamp()
    {
        var tracker = new GitHubRateLimitTracker();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        tracker.PauseUntil("GitHub", until);

        Assert.Equal(until, tracker.GetPausedUntil("GitHub"));
    }

    [Fact]
    public void GetPausedUntil_ReturnsNullOncePauseHasExpired()
    {
        var tracker = new GitHubRateLimitTracker();
        tracker.PauseUntil("GitHub", DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.Null(tracker.GetPausedUntil("GitHub"));
    }

    [Fact]
    public void PauseUntil_DoesNotShortenAnExistingLongerPause()
    {
        var tracker = new GitHubRateLimitTracker();
        var longer = DateTimeOffset.UtcNow.AddMinutes(10);
        var shorter = DateTimeOffset.UtcNow.AddMinutes(1);

        tracker.PauseUntil("GitHub", longer);
        tracker.PauseUntil("GitHub", shorter);

        Assert.Equal(longer, tracker.GetPausedUntil("GitHub"));
    }

    [Fact]
    public void PauseGates_AreIndependentPerConnector()
    {
        var tracker = new GitHubRateLimitTracker();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        tracker.PauseUntil("GitHub", until);

        Assert.Null(tracker.GetPausedUntil("OtherConnector"));
    }
}
