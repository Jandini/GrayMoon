using GrayMoon.Agent.Services.GitChanges;

namespace GrayMoon.Agent.Tests;

public sealed class GitChangesSnapshotCacheTests
{
    [Fact]
    public void NextVersion_is_strictly_increasing_across_rapid_calls()
    {
        var cache = new GitChangesSnapshotCache();
        const string repoPath = @"C:\repos\repo-a";

        var previous = cache.NextVersion(repoPath);
        for (var i = 0; i < 100; i++)
        {
            var next = cache.NextVersion(repoPath);
            Assert.True(next > previous, $"Expected version {next} to be greater than {previous}.");
            previous = next;
        }
    }

    [Fact]
    public void NextVersion_from_a_fresh_cache_exceeds_a_version_from_a_prior_cache_instance()
    {
        const string repoPath = @"C:\repos\repo-a";

        var priorSession = new GitChangesSnapshotCache();
        var lastVersionBeforeRestart = priorSession.NextVersion(repoPath);

        // A restarted Agent process constructs a brand-new, empty cache - simulate that instead of
        // reusing the same instance, since an old counter-based implementation would restart at 1 here.
        var restartedSession = new GitChangesSnapshotCache();
        var firstVersionAfterRestart = restartedSession.NextVersion(repoPath);

        Assert.True(
            firstVersionAfterRestart > lastVersionBeforeRestart,
            $"Expected the post-restart version {firstVersionAfterRestart} to exceed the pre-restart version {lastVersionBeforeRestart}.");
    }

    [Fact]
    public void NextVersion_from_a_fresh_cache_dwarfs_a_legacy_small_integer_high_water_mark()
    {
        var cache = new GitChangesSnapshotCache();

        var firstVersion = cache.NextVersion(@"C:\repos\repo-a");

        Assert.True(firstVersion > new DateTime(2020, 1, 1).Ticks);
    }

    [Fact]
    public void NextVersion_tracks_independent_sequences_per_repository()
    {
        var cache = new GitChangesSnapshotCache();

        var repoAFirst = cache.NextVersion(@"C:\repos\repo-a");
        var repoBFirst = cache.NextVersion(@"C:\repos\repo-b");
        var repoASecond = cache.NextVersion(@"C:\repos\repo-a");
        var repoBSecond = cache.NextVersion(@"C:\repos\repo-b");

        Assert.True(repoASecond > repoAFirst);
        Assert.True(repoBSecond > repoBFirst);
    }
}
