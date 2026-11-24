using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.App.Tests.Policy.Characterization;
using GrayMoon.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The file-version check and the effective version decide from the HEAD commit a context has stored. These are the
/// paths that used to leave that commit old or kept one for a checkout that had moved: a state write that claimed to
/// have read the checkout without reporting its commit, and operations that moved HEAD with hooks off.
/// </summary>
public sealed class StoredHeadCommitPathsTests
{
    private const string OldSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task A_probed_identity_without_a_commit_clears_the_stored_commit()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            // A repository whose branch was read and has no commit. The writer used to keep the old commit here.
            await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>().ApplyAsync(
                special, ctx.WorkspaceId, ctx.RepositoryId,
                new RepositoryStateSnapshot { BranchName = "main", HeadCommit = null, IdentityProbed = true });
        }

        var state = await ReadStateAsync(ctx);
        Assert.Equal("main", state.BranchName);
        Assert.Null(state.HeadCommit);
    }

    [Fact]
    public async Task A_write_that_did_not_probe_identity_keeps_the_stored_commit()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>().ApplyAsync(
                special, ctx.WorkspaceId, ctx.RepositoryId,
                new RepositoryStateSnapshot { OutgoingCommits = 2, IncomingCommits = 0, CommitCountsProbed = true });
        }

        Assert.Equal(OldSha, (await ReadStateAsync(ctx)).HeadCommit);
    }

    [Fact]
    public async Task Creating_a_branch_that_already_exists_stores_the_commit_that_was_checked_out()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        // The branch was there: no base commit, and HEAD is now that branch's tip.
        ctx.WorkerBridge.Respond("CreateBranch", new
        {
            success = true,
            currentBranch = "feature/existing",
            alreadyExisted = true,
            headCommitSha = NewSha,
        });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
                .CreateBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/existing", "main");
            Assert.True(Assert.IsType<CreateBranchApiResult>(outcome.Body).Success);
        }

        var state = await ReadStateAsync(ctx);
        Assert.Equal("feature/existing", state.BranchName);
        Assert.Equal(NewSha, state.HeadCommit);
    }

    [Fact]
    public async Task A_notification_without_a_state_snapshot_is_ignored()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        var broadcastsBefore = ctx.Broadcasts.Count;

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(new RepositorySyncNotification
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = ctx.RepositoryId,
                Version = "9.9.9",
                Branch = "other",
            });
        }

        // It used to be applied with guessed probe markers: branch and version replaced, the commit left behind.
        var state = await ReadStateAsync(ctx);
        Assert.Equal("feature/x", state.BranchName);
        Assert.Equal(OldSha, state.HeadCommit);
        Assert.Equal(broadcastsBefore, ctx.Broadcasts.Count);
    }

    [Fact]
    public async Task A_return_to_default_that_fails_at_the_pull_stores_where_the_checkout_left_the_repository()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = false,
            ErrorMessage = "fatal: unable to access the remote",
            CurrentBranch = "main",
            DefaultBranch = "main",
            State = new RepositoryStateSnapshot
            {
                BranchName = "main",
                HeadCommit = NewSha,
                DefaultBranchName = "main",
                IdentityProbed = true,
            },
        });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().ReturnToDefaultDirectAsync(
                ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x",
                deleteRemoteBranch: false, allowForceDeleteLocalBranch: false, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Equal("fatal: unable to access the remote", outcome.ErrorMessage);
        }

        var state = await ReadStateAsync(ctx);
        Assert.Equal("main", state.BranchName);
        Assert.Equal(NewSha, state.HeadCommit);
    }

    [Fact]
    public async Task A_return_to_default_that_reports_no_state_fails_and_changes_nothing()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            GitVersion = "2.0.0",
        });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().ReturnToDefaultDirectAsync(
                ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x",
                deleteRemoteBranch: false, allowForceDeleteLocalBranch: false, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("no repository state", outcome.ErrorMessage);
        }

        var state = await ReadStateAsync(ctx);
        Assert.Equal("feature/x", state.BranchName);
        Assert.Equal(OldSha, state.HeadCommit);
    }

    [Fact]
    public async Task A_pull_that_failed_before_the_checkout_was_read_keeps_branch_and_commit()
    {
        await using var ctx = await CreateWithStoredCommitAsync();
        // The Worker found no folder: it read nothing, so it reports no state.
        ctx.WorkerBridge.Respond("CommitSyncRepository", new { success = false, branch = "other", version = "9.9.9", errorMessage = "Repository not found" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>().CommitSyncAsync(
                ctx.WorkspaceId,
                special,
                ctx.RepositoryId,
                CancellationToken.None,
                new Progress<OperationProgress>(_ => { }),
                (_, _) => { },
                _ => { });
        }

        var state = await ReadStateAsync(ctx);
        Assert.Equal("feature/x", state.BranchName);
        Assert.Equal(OldSha, state.HeadCommit);
        Assert.Equal(RepoSyncStatus.Error, state.SyncStatus);
    }

    [Fact]
    public async Task Update_reports_a_committed_repository_whose_state_could_not_be_refreshed()
    {
        // Version files only (no dependency graph): one out-of-date file in the consumer is updated and committed,
        // with hooks off, and the refresh that should store the new commit fails.
        await using var s = await EffectiveVersionScenario.CreateAsync(dotNet: false, configureServices: PolicyHarness.AddFacades);
        await s.WithDbAsync(async db =>
        {
            var file = new WorkspaceFile { WorkspaceId = s.Ctx.WorkspaceId, RepositoryId = s.ConsumerId, FilePath = "version.props", FileName = "version.props" };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig
            {
                FileId = file.FileId,
                VersionPattern = $"API_BRANCH={{@{EffectiveVersionScenario.ProducerName}:branch}}",
            });
        });
        await s.MutateStateAsync(s.ConsumerLinkId, state => state.OutOfDateFileRepos = 1);
        s.Ctx.WorkerBridge.Respond("UpdateFileVersions", new { updatedCount = 1 });
        s.Ctx.WorkerBridge.Respond("StageAndCommit", new { success = true, committed = true });
        s.Ctx.WorkerBridge.Respond("RefreshRepositoryVersion", data: null, success: false, error: "The folder of repository graymoon-web was not found.");
        var repoErrors = new List<(int RepositoryId, string Message)>();

        await using var scope = s.Ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>().RunAsync(
            s.Ctx.WorkspaceId,
            s.Special,
            CancellationToken.None,
            progress: null,
            onRepoError: (repositoryId, message) => repoErrors.Add((repositoryId, message)),
            onLevelError: (_, _) => { });

        Assert.False(result.Success);
        Assert.Contains(s.Ctx.WorkerBridge.Calls, c => c.Command == "StageAndCommit");
        var error = Assert.Single(repoErrors);
        Assert.Equal(s.ConsumerId, error.RepositoryId);
        Assert.Equal(
            DependencyUpdateOrchestrator.StateNotRefreshedMessage("The folder of repository graymoon-web was not found."),
            error.Message);
        Assert.EndsWith("were not refreshed. Sync the repository.", error.Message);
    }

    private static async Task<SyncStateTestContext> CreateWithStoredCommitAsync()
    {
        var ctx = await SyncStateTestContext.CreateAsync(configureServices: PolicyHarness.AddFacades);
        var special = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var state = await db.WorkspaceRepositoryContextStates
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        state.BranchName = "feature/x";
        state.HeadCommit = OldSha;
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }
}
