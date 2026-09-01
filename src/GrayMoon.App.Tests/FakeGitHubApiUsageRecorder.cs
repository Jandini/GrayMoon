namespace GrayMoon.App.Tests;

/// <summary>No-op usage recorder for tests that construct <c>GitHubService</c> directly and don't care about usage counters.</summary>
public sealed class FakeGitHubApiUsageRecorder : IGitHubApiUsageRecorder
{
    public void Record(int connectorId, string connectorName, string requestUri, bool isNotModified, bool isError)
    {
    }

    public IReadOnlyList<GitHubApiUsageSnapshotEntry> TakeSnapshot() => [];
}
