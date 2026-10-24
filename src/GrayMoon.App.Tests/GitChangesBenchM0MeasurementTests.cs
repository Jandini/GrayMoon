using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

/// <summary>
/// One-shot M0 harness: times App persist / read / tree-build for synthetic inventories.
/// Run: <c>dotnet test --filter GitChangesBenchM0 --logger "console;verbosity=detailed"</c>
/// </summary>
public sealed class GitChangesBenchM0MeasurementTests
{
    public static TheoryData<int> Sizes => new() { 100, 2_000, 5_000, 25_000, 50_000 };

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Measure_persist_read_and_tree_for_path_count(int pathCount)
    {
        await using var ctx = await GitChangesTestDbContext.CreateAsync();
        var hubContext = new FakeHubContext<WorkspaceSyncHub>();
        var handler = GitChangesPushHandlerTestFactory.Create(
            ctx,
            hubContext,
            options: new GitChangesOptions { WriteLegacyGitChangesTables = false });

        var changes = new GitChangeEntry[pathCount];
        var pad = Math.Max(4, pathCount.ToString().Length);
        for (var i = 0; i < pathCount; i++)
        {
            var bucket = (i % 100).ToString("D2");
            changes[i] = new GitChangeEntry
            {
                Path = $"files/{bucket}/f{i.ToString().PadLeft(pad, '0')}.txt",
                IndexChange = GitChangeKind.None,
                WorktreeChange = GitChangeKind.Modified,
                IsTracked = false,
            };
        }

        var notification = new GitChangesSnapshotNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Snapshot = new GitChangeSnapshot
            {
                Version = 1,
                BranchName = "main",
                HeadCommit = "abc123",
                Changes = changes,
                ScannedAt = DateTimeOffset.UtcNow,
            },
        };

        var payloadUtf8Bytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(notification, GitChangesBench.PayloadSizeJsonOptions));

        var persistSw = Stopwatch.StartNew();
        await handler.HandleAsync(notification, CancellationToken.None);
        persistSw.Stop();

        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var readService = new WorkspaceGitChangesReadService(factory);
        var contextId = await new WorkspaceFeatureContextResolver(factory)
            .GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);

        var readSw = Stopwatch.StartNew();
        var view = await readService.GetContextAsync(ctx.WorkspaceId, contextId, CancellationToken.None);
        readSw.Stop();

        var buildSw = Stopwatch.StartNew();
        var rows = GitChangesTreeBuilder.Build(view, filterQuery: null);
        buildSw.Stop();

        // xUnit captures this via the test runner console when verbosity is detailed.
        Console.WriteLine(
            $"GitChangesBenchM0 paths={pathCount} SnapshotBytes={payloadUtf8Bytes} PersistMs={persistSw.ElapsedMilliseconds} ReadMs={readSw.ElapsedMilliseconds} BuildTreeMs={buildSw.ElapsedMilliseconds} RowsRendered={rows.Count}");

        Assert.Equal(pathCount, view.Repositories.Sum(r => r.Changes.Count));
        Assert.True(rows.Count > 0);
    }
}
