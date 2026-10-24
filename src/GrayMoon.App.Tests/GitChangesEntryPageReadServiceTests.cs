using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class GitChangesEntryPageReadServiceTests
{
    [Fact]
    public async Task GetContextSummariesAsync_omits_entry_lists_but_counts_paths()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistPathsAsync(ctx, factory, version: 1, "f0.txt", "f1.txt", "f2.txt", "f3.txt", "f4.txt");

        var contextId = await SpecialContextIdAsync(ctx, factory);
        var readService = new WorkspaceGitChangesReadService(factory);
        var summaries = await readService.GetContextSummariesAsync(
            ctx.WorkspaceId,
            contextId,
            CancellationToken.None);

        Assert.Equal(5, summaries.TotalEntryCount);
        Assert.Single(summaries.Repositories);
    }

    [Fact]
    public async Task GetEntryPageAsync_pages_direct_children_with_keyset()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistPathsAsync(ctx, factory, version: 1,
            "a.txt", "b.txt", "c.txt", "d.txt", "folder/x.cs", "folder/y.cs");

        var contextId = await SpecialContextIdAsync(ctx, factory);
        var readService = new WorkspaceGitChangesReadService(factory);
        var page1 = await readService.GetEntryPageAsync(
            ctx.WorkspaceId,
            contextId,
            ctx.WorkspaceRepositoryId,
            isStagedSection: false,
            pathPrefix: string.Empty,
            afterChildKey: null,
            take: 2,
            filter: null,
            CancellationToken.None);

        Assert.Equal(5, page1.TotalDirectChildCount);
        Assert.True(page1.HasMore);
        Assert.Equal(2, page1.Children.Count);

        var page2 = await readService.GetEntryPageAsync(
            ctx.WorkspaceId,
            contextId,
            ctx.WorkspaceRepositoryId,
            isStagedSection: false,
            pathPrefix: string.Empty,
            afterChildKey: page1.NextAfterChildKey,
            take: 10,
            filter: null,
            CancellationToken.None);

        Assert.False(page2.HasMore);
        var allNames = page1.Children.Concat(page2.Children).Select(c => c.Name).ToList();
        Assert.Equal(["folder", "a.txt", "b.txt", "c.txt", "d.txt"], allNames);
    }

    private static Task<WorkspaceFeatureContextId> SpecialContextIdAsync(
        GitChangesTestDbContext ctx,
        GitChangesTestDbContext.TestDbContextFactory factory) =>
        new WorkspaceFeatureContextResolver(factory).GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);

    private static async Task PersistPathsAsync(
        GitChangesTestDbContext ctx,
        GitChangesTestDbContext.TestDbContextFactory factory,
        long version,
        params string[] paths)
    {
        var entries = paths.Select(p => new GitChangeEntry
        {
            Path = p,
            IndexChange = GitChangeKind.None,
            WorktreeChange = GitChangeKind.Modified,
        }).ToArray();

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
                Changes = entries,
                ScannedAt = DateTimeOffset.UtcNow,
            },
        }, CancellationToken.None);
    }
}
