using GrayMoon.App.Hubs;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class GitChangesContentDigestTests
{
    private static GitChangeEntry Entry(string path, GitChangeKind worktree = GitChangeKind.Modified) => new()
    {
        Path = path,
        WorktreeChange = worktree,
    };

    private static string Digest(IEnumerable<GitChangeEntry> entries, string? branch = "main", int? insertions = null) =>
        GitChangesContentDigest.Compute(
            entries, branch, "abc", false, false, false, false, false,
            stagedCount: 0, changedCount: 3, conflictCount: 0,
            insertions, null, null, null);

    [Fact]
    public void Same_entries_in_any_order_give_the_same_digest()
    {
        var a = Digest([Entry("a.txt"), Entry("b/c.txt"), Entry("z.txt")]);
        var b = Digest([Entry("z.txt"), Entry("a.txt"), Entry("b/c.txt")]);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Changed_path_kind_or_status_changes_the_digest()
    {
        var baseline = Digest([Entry("a.txt"), Entry("b.txt")]);

        Assert.NotEqual(baseline, Digest([Entry("a.txt"), Entry("c.txt")]));
        Assert.NotEqual(baseline, Digest([Entry("a.txt"), Entry("b.txt", GitChangeKind.Deleted)]));
        Assert.NotEqual(baseline, Digest([Entry("a.txt"), Entry("b.txt")], branch: "other"));
        Assert.NotEqual(baseline, Digest([Entry("a.txt"), Entry("b.txt")], insertions: 5));
    }

    [Fact]
    public async Task Identical_content_at_a_newer_version_keeps_the_digest_and_broadcasts_it()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = GitChangesPushHandlerTestFactory.Create(ctx, hubContext);

        GitChangeSnapshot Snapshot(long version, params GitChangeEntry[] entries) => new()
        {
            Version = version,
            BranchName = "main",
            HeadCommit = "abc123",
            Changes = entries,
            ScannedAt = DateTimeOffset.UtcNow,
        };

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = Snapshot(1, Entry("a.txt"), Entry("b.txt")),
        }, CancellationToken.None);
        var first = Assert.Single(ctx.DbContext.WorkspaceGitContextRepositoryStatuses);
        var firstDigest = first.ContentDigest;
        Assert.False(string.IsNullOrEmpty(firstDigest));
        Assert.Equal(2, first.PathCount);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = Snapshot(2, Entry("b.txt"), Entry("a.txt")),
        }, CancellationToken.None);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = Snapshot(3, Entry("a.txt")),
        }, CancellationToken.None);

        var broadcasts = hubContext.ClientsImpl.AllProxy.Sent
            .Where(s => s.Method == "ContextGitChangesUpdated")
            .ToList();
        Assert.Equal(3, broadcasts.Count);
        Assert.Equal(ctx.RepositoryId, broadcasts[0].Args[2]);
        Assert.Equal(firstDigest, broadcasts[0].Args[3]);
        Assert.Equal(firstDigest, broadcasts[1].Args[3]);
        Assert.NotEqual(firstDigest, broadcasts[2].Args[3]);
    }
}
