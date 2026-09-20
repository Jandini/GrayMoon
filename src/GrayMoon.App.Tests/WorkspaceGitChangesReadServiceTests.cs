using GrayMoon.App.Hubs;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public class WorkspaceGitChangesReadServiceTests
{
    [Fact]
    public async Task Workspace_with_no_persisted_status_returns_an_empty_repository_list()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var readService = new WorkspaceGitChangesReadService(factory);

        var view = await readService.GetWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None);

        Assert.Equal(ctx.WorkspaceId, view.WorkspaceId);
        Assert.Empty(view.Repositories);
    }

    [Fact]
    public async Task Reading_does_not_require_any_agent_command()
    {
        // The read service's public surface is a pure SQLite query - there is no IAgentBridge dependency
        // at all, so opening/reloading the page can never issue an agent status command by construction.
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        IWorkspaceGitChangesReadService readService = new WorkspaceGitChangesReadService(factory);

        await readService.GetWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None);

        // No exception, no agent bridge required to construct the service - nothing further to assert.
    }

    [Fact]
    public async Task Returns_persisted_status_and_change_entries_for_the_repository()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistSnapshotAsync(ctx, factory, 1,
            new GitChangeEntry { Path = "file.txt", IndexChange = GitChangeKind.None, WorktreeChange = GitChangeKind.Modified });

        var readService = new WorkspaceGitChangesReadService(factory);
        var view = await readService.GetWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None);

        var repo = Assert.Single(view.Repositories);
        Assert.Equal(ctx.WorkspaceRepositoryId, repo.WorkspaceRepositoryId);
        Assert.Equal("graymoon-api", repo.RepositoryName);
        Assert.Equal("main", repo.BranchName);
        Assert.Equal(1, repo.ChangedCount);
        Assert.Null(repo.Insertions);
        Assert.Null(repo.Deletions);

        var change = Assert.Single(repo.Changes);
        Assert.Equal("file.txt", change.Path);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
    }

    [Fact]
    public async Task Does_not_return_repositories_from_a_different_workspace()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistSnapshotAsync(ctx, factory, 1,
            new GitChangeEntry { Path = "file.txt", WorktreeChange = GitChangeKind.Modified });

        var readService = new WorkspaceGitChangesReadService(factory);
        var view = await readService.GetWorkspaceAsync(ctx.WorkspaceId + 1, CancellationToken.None);

        Assert.Empty(view.Repositories);
    }

    [Fact]
    public async Task Returns_persisted_line_stats()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistSnapshotAsync(ctx, factory, 1,
            [new GitChangeEntry { Path = "file.txt", WorktreeChange = GitChangeKind.Modified }],
            insertions: 8,
            deletions: 3,
            stagedInsertions: 5,
            stagedDeletions: 1);

        var readService = new WorkspaceGitChangesReadService(factory);
        var view = await readService.GetWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None);

        var repo = Assert.Single(view.Repositories);
        Assert.Equal(8, repo.Insertions);
        Assert.Equal(3, repo.Deletions);
        Assert.Equal(5, repo.StagedInsertions);
        Assert.Equal(1, repo.StagedDeletions);
    }

    private static Task PersistSnapshotAsync(
        GitChangesTestDbContext ctx,
        GitChangesTestDbContext.TestDbContextFactory factory,
        long version,
        params GitChangeEntry[] changes) =>
        PersistSnapshotAsync(ctx, factory, version, changes, insertions: null, deletions: null, stagedInsertions: null, stagedDeletions: null);

    private static async Task PersistSnapshotAsync(
        GitChangesTestDbContext ctx,
        GitChangesTestDbContext.TestDbContextFactory factory,
        long version,
        GitChangeEntry[] changes,
        int? insertions,
        int? deletions,
        int? stagedInsertions = null,
        int? stagedDeletions = null)
    {
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = GitChangesPushHandlerTestFactory.Create(ctx, hubContext);

        await handler.HandleAsync(new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = new GitChangeSnapshot
            {
                Version = version,
                BranchName = "main",
                HeadCommit = "abc123",
                Changes = changes,
                ScannedAt = DateTimeOffset.UtcNow,
                Insertions = insertions,
                Deletions = deletions,
                StagedInsertions = stagedInsertions,
                StagedDeletions = stagedDeletions,
            },
        }, CancellationToken.None);
    }
}
