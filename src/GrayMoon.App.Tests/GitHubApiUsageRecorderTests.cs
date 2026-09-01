namespace GrayMoon.App.Tests;

public sealed class GitHubApiUsageRecorderTests
{
    [Fact]
    public void TakeSnapshot_AggregatesMultipleRecordsForSameConnectorCategory()
    {
        var recorder = new GitHubApiUsageRecorder();

        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: true, isError: false);
        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/2/jobs", isNotModified: false, isError: true);

        var snapshot = recorder.TakeSnapshot();
        var entry = Assert.Single(snapshot);

        Assert.Equal(1, entry.ConnectorId);
        Assert.Equal("acme", entry.ConnectorName);
        Assert.Equal(GitHubApiUsageCategoryMapper.Actions, entry.Category);
        Assert.Equal(3, entry.RequestCount);
        Assert.Equal(1, entry.NotModifiedCount);
        Assert.Equal(1, entry.ErrorCount);
    }

    [Fact]
    public void TakeSnapshot_DifferentCategories_ProduceSeparateEntries()
    {
        var recorder = new GitHubApiUsageRecorder();
        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        recorder.Record(1, "acme", "repos/acme/widgets/pulls?state=open", isNotModified: false, isError: false);

        var snapshot = recorder.TakeSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, e => e.Category == GitHubApiUsageCategoryMapper.Actions && e.RequestCount == 1);
        Assert.Contains(snapshot, e => e.Category == GitHubApiUsageCategoryMapper.PullRequests && e.RequestCount == 1);
    }

    [Fact]
    public void TakeSnapshot_WithNoRecordedCalls_ReturnsEmpty()
    {
        var recorder = new GitHubApiUsageRecorder();
        Assert.Empty(recorder.TakeSnapshot());
    }

    [Fact]
    public void TakeSnapshot_RemovesCountersSoNextSnapshotIsOnlyNewRecords()
    {
        var recorder = new GitHubApiUsageRecorder();
        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);

        var first = recorder.TakeSnapshot();
        Assert.Equal(1, Assert.Single(first).RequestCount);

        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        recorder.Record(1, "acme", "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);

        var second = recorder.TakeSnapshot();
        Assert.Equal(2, Assert.Single(second).RequestCount);
    }
}
