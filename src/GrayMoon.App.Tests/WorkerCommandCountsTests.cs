namespace GrayMoon.App.Tests;

/// <summary>Holds <see cref="WorkerCommandCounts"/> to what the command-count gates rely on.</summary>
public sealed class WorkerCommandCountsTests
{
    [Fact]
    public async Task Counts_recorded_commands_by_name_and_reads_their_arguments()
    {
        var bridge = new FakeWorkerBridge();
        bridge.Respond("FetchCommits", new { });
        bridge.Respond("SyncRepository", new { });

        await bridge.SendCommandAsync("SyncRepository", new { repositoryName = "before" });
        var before = WorkerCommandCounts.Of(bridge).Total;
        await bridge.SendCommandAsync("FetchCommits", new { repositoryName = "alpha", workspaceName = "ws" });
        await bridge.SendCommandAsync("fetchcommits", new Dictionary<string, object?> { ["RepositoryName"] = "beta" });
        await bridge.SendCommandAsync("InspectWorktree", new { worktreePath = "x" });

        var counts = WorkerCommandCounts.Since(bridge, before);

        Assert.Equal(3, counts.Total);
        Assert.Equal(2, counts.Count("FetchCommits"));
        Assert.Equal(["FetchCommits", "InspectWorktree"], counts.Commands);
        Assert.Equal(["alpha", "beta"], counts.ArgValues("FetchCommits", "repositoryName"));
        Assert.Equal([null], counts.ArgValues("InspectWorktree", "repositoryName"));
        counts.AssertNone("SyncRepository", "GetGitVersion");
        counts.AssertCount("InspectWorktree", 1);
        Assert.Equal("FetchCommits x2, InspectWorktree x1", counts.Describe());
        Assert.Equal(1, WorkerCommandCounts.Of(bridge).Count("SyncRepository"));
    }

    [Fact]
    public async Task A_command_that_was_sent_fails_the_none_assertion_with_the_command_list()
    {
        var bridge = new FakeWorkerBridge();
        await bridge.SendCommandAsync("SyncRepository", new { repositoryName = "alpha" });

        var failure = Assert.ThrowsAny<Exception>(() => WorkerCommandCounts.Of(bridge).AssertNone("SyncRepository"));

        Assert.Contains("SyncRepository x1", failure.Message, StringComparison.Ordinal);
    }
}
