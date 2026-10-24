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

    [Fact]
    public async Task GetEntryPageAsync_sql_grouping_matches_in_memory_grouping_for_nested_prefix_and_directory_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        string[] paths =
        [
            "src/a.cs", "src/B.cs", "src/Sub/c.cs", "src/Sub/d/e.cs", "src/nested-repo/", "src/Sub2.txt",
            "src2/x.cs", "src.txt", "docs/readme.md",
        ];
        await PersistPathsAsync(ctx, factory, version: 1, paths);

        var contextId = await SpecialContextIdAsync(ctx, factory);
        var readService = new WorkspaceGitChangesReadService(factory);

        var expected = WorkspaceGitChangesReadService.OrderChildren(
                WorkspaceGitChangesReadService.GroupDirectChildren(
                    "src",
                    paths.Select(p => new WorkspaceGitChangeEntryView { Path = p, WorktreeChange = GitChangeKind.Modified })))
            .Select(c => (c.ChildKey, c.IsFolder, c.IsDirectoryEntry, c.Path))
            .ToList();

        // Walk every page one child at a time: keyset must neither skip nor repeat.
        var actual = new List<(string ChildKey, bool IsFolder, bool IsDirectoryEntry, string Path)>();
        string? after = null;
        do
        {
            var page = await readService.GetEntryPageAsync(
                ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: false,
                pathPrefix: "src", afterChildKey: after, take: 1, filter: null, CancellationToken.None);
            Assert.Equal(expected.Count, page.TotalDirectChildCount);
            actual.AddRange(page.Children.Select(c => (c.ChildKey, c.IsFolder, c.IsDirectoryEntry, c.Path)));
            after = page.NextAfterChildKey;
        }
        while (after != null);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetEntryPathsAsync_returns_only_paths_under_the_prefix()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistPathsAsync(ctx, factory, version: 1, "src/a.cs", "src/sub/b.cs", "src2/c.cs", "src.txt");

        var contextId = await SpecialContextIdAsync(ctx, factory);
        var readService = new WorkspaceGitChangesReadService(factory);

        var folder = await readService.GetEntryPathsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: false, "src", CancellationToken.None);
        var all = await readService.GetEntryPathsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: false, string.Empty, CancellationToken.None);
        var staged = await readService.GetEntryPathsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: true, string.Empty, CancellationToken.None);

        Assert.Equal(["src/a.cs", "src/sub/b.cs"], folder);
        Assert.Equal(4, all.Count);
        Assert.Empty(staged);
    }

    [Fact]
    public async Task EntryExistsAsync_and_summary_kind_counts_reflect_persisted_entries()
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        await PersistPathsAsync(ctx, factory, version: 1, "a.txt", "b.txt");

        var contextId = await SpecialContextIdAsync(ctx, factory);
        var readService = new WorkspaceGitChangesReadService(factory);

        Assert.True(await readService.EntryExistsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: false, "a.txt", CancellationToken.None));
        Assert.False(await readService.EntryExistsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: true, "a.txt", CancellationToken.None));
        Assert.False(await readService.EntryExistsAsync(
            ctx.WorkspaceId, contextId, ctx.WorkspaceRepositoryId, isStagedSection: false, "missing.txt", CancellationToken.None));

        var summaries = await readService.GetContextSummariesAsync(ctx.WorkspaceId, contextId, CancellationToken.None);
        Assert.Equal(2, summaries.Repositories.Single().ChangedKindCounts![GitChangeKind.Modified]);

        var repo = await readService.GetRepositorySummaryAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, CancellationToken.None);
        Assert.NotNull(repo);
        Assert.Empty(repo!.Changes);
        Assert.Equal(2, repo.ChangedKindCounts![GitChangeKind.Modified]);
    }

    [Fact]
    public void NodeCache_ignores_a_page_from_another_snapshot_version_and_never_duplicates_children()
    {
        var cache = new GitChangesTreeNodeCache.PrefixPageCache();
        var child = new GitChangesTreeChild { IsFolder = false, Name = "a.txt", Path = "a.txt" };
        var page = new GitChangesEntryPage { SnapshotVersion = 3, TotalDirectChildCount = 1, Children = [child] };

        Assert.True(cache.ApplyPage(page));
        Assert.True(cache.ApplyPage(page));
        Assert.Single(cache.Children);
        Assert.True(cache.IsComplete);

        Assert.False(cache.ApplyPage(page with { SnapshotVersion = 4 }));
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
