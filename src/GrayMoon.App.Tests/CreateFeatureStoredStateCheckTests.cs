using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Create Feature copies the Workspace's stored state into the new Feature. Before it does, it compares that state
/// with the HEAD snapshot it has just read and synchronizes only the repositories that differ; the removal queue
/// no longer synchronizes the whole Workspace for it.
/// </summary>
public sealed class CreateFeatureStoredStateCheckTests
{
    private const string ApiSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UiSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string UiOldSha = "0000000000000000000000000000000000000001";

    [Fact]
    public async Task Create_with_current_stored_state_synchronizes_nothing()
    {
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();

        var result = await CreateAsync(ctx);

        Assert.True(result.Success, result.Error);
        WorkerCommandCounts.Of(ctx.WorkerBridge).AssertNone("SyncRepository", "RefreshRepositoryVersion", "FetchCommits");
    }

    [Fact]
    public async Task Create_synchronizes_exactly_the_repository_whose_stored_commit_differs_before_it_seeds()
    {
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        // A commit GrayMoon did not see: the stored state of graymoon-ui is one commit behind its checkout.
        await SetStoredUiStateAsync(ctx, s => s.HeadCommit = UiOldSha);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(UiSha, "release/3.0", incoming: 5));

        var result = await CreateAsync(ctx);

        Assert.True(result.Success, result.Error);
        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertCount("SyncRepository", 1);
        Assert.Equal(["graymoon-ui"], counts.ArgValues("SyncRepository", "repositoryName"));

        // Before the seed: no worktree existed yet when the repository was synchronized.
        var commands = ctx.WorkerBridge.Calls.Select(c => c.Command).ToList();
        Assert.True(
            commands.IndexOf("SyncRepository") < commands.IndexOf(WorkerHubMethods.CreateGitWorktree),
            $"Expected the Sync before the first worktree: {string.Join(", ", commands)}");

        // The Feature was seeded from the synchronized state, not from the stale one.
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uiLinkId = await UiLinkIdAsync(db, ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var stored = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == uiLinkId);
        Assert.Equal(UiSha, stored.HeadCommit);
        var seeded = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == result.ContextId!.Value.Value && s.WorkspaceRepositoryId == uiLinkId);
        Assert.Equal(5, seeded.IncomingCommits);
        Assert.Equal(UiSha, seeded.HeadCommit);
    }

    [Fact]
    public async Task Create_synchronizes_a_repository_that_was_switched_to_another_branch_at_the_same_commit()
    {
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        await SetStoredUiStateAsync(ctx, s => s.BranchName = "main");
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(UiSha, "release/3.0", incoming: 0));

        var result = await CreateAsync(ctx);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["graymoon-ui"], WorkerCommandCounts.Of(ctx.WorkerBridge).ArgValues("SyncRepository", "repositoryName"));
    }

    [Fact]
    public async Task Create_synchronizes_a_repository_that_has_no_stored_commit()
    {
        // No evidence is not "current": a Workspace that never recorded a commit for the repository is read first.
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        await SetStoredUiStateAsync(ctx, s => s.HeadCommit = null);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(UiSha, "release/3.0", incoming: 0));

        var result = await CreateAsync(ctx);

        Assert.True(result.Success, result.Error);
        WorkerCommandCounts.Of(ctx.WorkerBridge).AssertCount("SyncRepository", 1);
    }

    [Fact]
    public async Task Create_fails_naming_the_repository_when_its_synchronization_fails_and_creates_nothing()
    {
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        await SetStoredUiStateAsync(ctx, s => s.HeadCommit = UiOldSha);
        ctx.WorkerBridge.Respond("SyncRepository", new { success = false, errorMessage = "Git fetch failed: could not resolve host." });

        var result = await CreateAsync(ctx);

        Assert.False(result.Success);
        Assert.Equal("WorkspaceStateNotCurrent", result.Condition);
        Assert.Contains("graymoon-ui", result.Error, StringComparison.Ordinal);
        Assert.Contains("could not resolve host", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("graymoon-api", result.Error, StringComparison.Ordinal);
        WorkerCommandCounts.Of(ctx.WorkerBridge).AssertNone(WorkerHubMethods.CreateGitWorktree);

        // Nothing was seeded from the stale state: no Feature, no context, and the stale state row is as it was.
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync());
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.Kind == WorkspaceFeatureContextKind.Feature));
        var uiLinkId = await UiLinkIdAsync(db, ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var stored = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == uiLinkId);
        Assert.Equal(UiOldSha, stored.HeadCommit);
    }

    [Fact]
    public async Task Create_fails_when_the_checkout_moved_again_while_it_was_synchronized()
    {
        await using var ctx = await NewWorkspaceAsync();
        await ctx.StoreHeadSnapshotAsCurrentStateAsync();
        await SetStoredUiStateAsync(ctx, s => s.HeadCommit = UiOldSha);
        // The Sync reads a third commit: the snapshot Create holds is no longer what is checked out.
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("cccccccccccccccccccccccccccccccccccccccc", "release/3.0", incoming: 0));

        var result = await CreateAsync(ctx);

        Assert.False(result.Success);
        Assert.Equal("WorkspaceStateNotCurrent", result.Condition);
        Assert.Contains("graymoon-ui", result.Error, StringComparison.Ordinal);
        Assert.Contains("changed again", result.Error, StringComparison.Ordinal);
        WorkerCommandCounts.Of(ctx.WorkerBridge).AssertNone(WorkerHubMethods.CreateGitWorktree);
        await using var scope = ctx.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkspaceFeatures.AnyAsync());
    }

    private static async Task<CreateFeatureResult> CreateAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
            .CreateFeatureAsync(ctx.WorkspaceId, "feat-check", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
    }

    /// <summary>Two repositories (graymoon-api on develop, graymoon-ui on release/3.0) with canned HEAD, worktree and file-check answers.</summary>
    private static async Task<SyncStateTestContext> NewWorkspaceAsync()
    {
        var ctx = await SyncStateTestContext.CreateAsync();
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
            var ui = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = "graymoon-ui",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = "https://github.com/acme/graymoon-ui.git",
            };
            db.Repositories.Add(ui);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = ui.RepositoryId,
                GitVersion = "1.0.0",
                BranchName = "release/3.0",
                DefaultBranchName = "main",
                IncomingCommits = 0,
                SyncStatus = RepoSyncStatus.InSync,
                RepositoryType = ProjectType.Library,
            });
            await db.SaveChangesAsync();
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string> { ["graymoon-api"] = ApiSha, ["graymoon-ui"] = UiSha },
            branches = new Dictionary<string, string> { ["graymoon-api"] = "develop", ["graymoon-ui"] = "release/3.0" },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });
        ctx.WorkerBridge.Respond(WorkerHubMethods.HoldWorktreeHookSyncs, new { success = true });
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });
        return ctx;
    }

    private static object SyncResponse(string commit, string branch, int incoming) => new
    {
        success = true,
        version = "3.0.1",
        branch,
        commit,
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = incoming,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { branch, "main" },
        remoteBranches = new[] { "main", branch },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };

    private static Task<int> UiLinkIdAsync(AppDbContext db, SyncStateTestContext ctx)
        => db.WorkspaceRepositories
            .Where(l => l.WorkspaceId == ctx.WorkspaceId && l.Repository!.RepositoryName == "graymoon-ui")
            .Select(l => l.WorkspaceRepositoryId)
            .SingleAsync();

    private static async Task SetStoredUiStateAsync(SyncStateTestContext ctx, Action<WorkspaceRepositoryContextState> mutate)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uiLinkId = await UiLinkIdAsync(db, ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var state = await db.WorkspaceRepositoryContextStates
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == uiLinkId);
        mutate(state);
        await db.SaveChangesAsync();
    }
}
