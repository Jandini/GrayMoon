using GrayMoon.App.Hubs;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public class GitChangesSnapshotPushHandlerTests
{
    private static GitChangeSnapshot MakeSnapshot(long version, params GitChangeEntry[] changes) => new()
    {
        Version = version,
        BranchName = "main",
        HeadCommit = "abc123",
        Changes = changes,
        ScannedAt = DateTimeOffset.UtcNow,
    };

    private static GitChangeEntry MakeEntry(string path, GitChangeKind index = GitChangeKind.None, GitChangeKind worktree = GitChangeKind.Modified) => new()
    {
        Path = path,
        IndexChange = index,
        WorktreeChange = worktree,
    };

    [Fact]
    public async Task First_snapshot_creates_status_and_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        var notification = new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(1, MakeEntry("file.txt")),
        };

        await handler.HandleAsync(notification, CancellationToken.None);

        var status = Assert.Single(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Equal(ctx.WorkspaceRepositoryId, status.WorkspaceRepositoryId);
        Assert.Equal(1, status.SnapshotVersion);
        Assert.Equal("main", status.BranchName);
        Assert.Equal(1, status.ChangedCount);

        var entry = Assert.Single(ctx.DbContext.WorkspaceGitChangeEntries);
        Assert.Equal("file.txt", entry.Path);

        Assert.Single(hubContext.ClientsImpl.AllProxy.Sent, s => s.Method == "GitChangesUpdated");
    }

    [Fact]
    public async Task Newer_snapshot_replaces_status_and_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(1, MakeEntry("old.txt")),
        }, CancellationToken.None);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(2, MakeEntry("new.txt"), MakeEntry("new2.txt")),
        }, CancellationToken.None);

        var status = Assert.Single(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Equal(2, status.SnapshotVersion);

        var entries = ctx.DbContext.WorkspaceGitChangeEntries.ToList();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.Path == "old.txt");
    }

    [Fact]
    public async Task Older_or_equal_snapshot_version_is_rejected()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(5, MakeEntry("current.txt")),
        }, CancellationToken.None);

        hubContext.ClientsImpl.AllProxy.Sent.Clear();

        // Equal version: rejected
        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(5, MakeEntry("stale-equal.txt")),
        }, CancellationToken.None);

        // Older version: rejected
        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(3, MakeEntry("stale-older.txt")),
        }, CancellationToken.None);

        var status = Assert.Single(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Equal(5, status.SnapshotVersion);

        var entry = Assert.Single(ctx.DbContext.WorkspaceGitChangeEntries);
        Assert.Equal("current.txt", entry.Path);

        Assert.Empty(hubContext.ClientsImpl.AllProxy.Sent);
    }

    [Fact]
    public async Task Snapshot_with_no_changes_clears_previous_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(1, MakeEntry("file.txt")),
        }, CancellationToken.None);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = MakeSnapshot(2),
        }, CancellationToken.None);

        Assert.Empty(ctx.DbContext.WorkspaceGitChangeEntries);
        var status = Assert.Single(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Equal(0, status.ChangedCount);
    }

    [Fact]
    public async Task Unknown_workspace_repository_is_a_no_op()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = 999_999,
            RepositoryId = 999_999,
            Snapshot = MakeSnapshot(1, MakeEntry("file.txt")),
        }, CancellationToken.None);

        Assert.Empty(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Empty(hubContext.ClientsImpl.AllProxy.Sent);
    }

    [Fact]
    public async Task Staged_changed_and_conflict_counts_are_computed_from_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = new GitChangesSnapshotPushHandler(factory, hubContext, NullLogger<GitChangesSnapshotPushHandler>.Instance);

        var snapshot = MakeSnapshot(
            1,
            new GitChangeEntry { Path = "staged.txt", IndexChange = GitChangeKind.Added, WorktreeChange = GitChangeKind.None },
            new GitChangeEntry { Path = "changed.txt", IndexChange = GitChangeKind.None, WorktreeChange = GitChangeKind.Modified },
            new GitChangeEntry { Path = "both.txt", IndexChange = GitChangeKind.Modified, WorktreeChange = GitChangeKind.Modified },
            new GitChangeEntry { Path = "conflict.txt", IndexChange = GitChangeKind.Unmerged, WorktreeChange = GitChangeKind.Unmerged, IsConflicted = true });

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = snapshot,
        }, CancellationToken.None);

        var status = Assert.Single(ctx.DbContext.WorkspaceGitRepositoryStatuses);
        Assert.Equal(3, status.StagedCount); // staged.txt, both.txt, conflict.txt (unmerged counts as an index change too)
        Assert.Equal(3, status.ChangedCount); // changed.txt, both.txt, conflict.txt
        Assert.Equal(1, status.ConflictCount); // conflict.txt
    }
}
