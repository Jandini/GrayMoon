using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Git;
using GrayMoon.Application;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// An inherited effective version holds only while the repository's HEAD is its branch point. Update and Push commits a
/// level and then refreshes the versions of the committed repositories; that refresh must store the new HEAD, or the
/// inheritance outlives the commit and the next level's <c>{@Repo}</c> version files are judged up to date against the
/// parent version (log 2026-11-24 PUSH-UPDATE-DOES-NOT-COMPLETE: levels 5 and 6 were only updated by a second run).
/// </summary>
public sealed class EffectiveVersionHeadCommitTests
{
    private const string NewVersion = "2.3.1-abc.4";

    [Fact]
    public async Task A_version_refresh_after_a_commit_stores_the_new_HEAD_and_ends_the_inheritance()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var inherited = await s.ResolveAsync();
        Assert.Equal(EffectiveVersionKind.InheritedVerified, inherited.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);

        s.Ctx.WorkerBridge.Respond("RefreshRepositoryVersion", new
        {
            version = NewVersion,
            branch = "feature/x",
            commit = EffectiveVersionScenario.MovedSha,
            outgoingCommits = 1,
            incomingCommits = 0,
            hasUpstream = true,
            defaultBranchBehind = 0,
            defaultBranchAhead = 1,
        });

        await using (var scope = s.Ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (success, error) = await git.SyncSingleRepositoryAsync(s.ProducerId, s.Ctx.WorkspaceId, s.Special);
            Assert.True(success, error);
        }

        Assert.Equal(EffectiveVersionScenario.MovedSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
        var after = (await s.ResolveAsync(bypassCache: false)).Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, after.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.NotOnBaseline, after.ReasonCode);
        Assert.Equal(NewVersion, after.EffectiveVersion);
    }

    [Fact]
    public async Task A_sync_on_a_branch_stores_the_HEAD_it_reports()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();

        s.Ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = true,
            version = NewVersion,
            branch = "feature/x",
            commit = EffectiveVersionScenario.MovedSha,
            defaultBranch = "main",
            outgoingCommits = 1,
            incomingCommits = 0,
            defaultBranchBehind = 0,
            defaultBranchAhead = 1,
            hasUpstream = true,
            upstreamProbed = true,
            localBranches = new[] { "feature/x", "main" },
            remoteBranches = new[] { "origin/feature/x", "origin/main" },
            tags = Array.Empty<string>(),
        });

        await using (var scope = s.Ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.SyncAsync(s.Ctx.WorkspaceId, s.Special, repositoryIds: [s.ProducerId]);
        }

        Assert.Equal(EffectiveVersionScenario.MovedSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
    }

    [Fact]
    public async Task A_branch_checkout_stores_the_HEAD_the_Worker_reports()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();
        s.Ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse
        {
            Success = true,
            CurrentBranch = "feature/y",
            Commit = EffectiveVersionScenario.MovedSha,
        });

        await using (var scope = s.Ctx.CreateScope())
        {
            var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
            var outcome = await ops.CheckoutAsync(s.Ctx.WorkspaceId, s.Special, s.ProducerId, "feature/y", isTag: false);
            Assert.True(outcome.IsSuccessStatus);
        }

        var state = await s.ReadStateAsync(s.ProducerLinkId);
        Assert.Equal("feature/y", state.BranchName);
        Assert.Equal(EffectiveVersionScenario.MovedSha, state.HeadCommit);
    }
}
