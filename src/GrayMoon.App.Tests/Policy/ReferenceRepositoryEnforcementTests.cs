using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// What the services do with a read-only (reference) repository. The flag is seeded in the database: nothing in the
/// product can set it yet. Each test seeds unpushed commits or changes, so a refusal is the policy's and not the
/// absence of work. The producer is the read-only repository; the consumer stays writable.
/// </summary>
public sealed class ReferenceRepositoryEnforcementTests
{
    [Fact]
    public async Task A_push_leaves_the_read_only_repository_out_and_pushes_the_writable_one()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        RespondPushCommands(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().PushAsync(
            ctx.WorkspaceId,
            special,
            new HashSet<int> { graph.ProducerId, graph.ConsumerId },
            synchronizedPush: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            restorePackages: false);

        var skip = Assert.Single(result.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.Reference, skip.Reason);
        Assert.Equal(RepositoryOperationPolicy.ReferenceMessage, skip.Message);
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "PushRepository"));
    }

    [Fact]
    public async Task A_pull_asks_the_Worker_for_pull_only_in_the_read_only_repository_alone()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>();
        await sync.PullLevelAsync(ctx.WorkspaceId, special, [graph.ProducerId, graph.ConsumerId], null, CancellationToken.None);
        await sync.PullAsync(ctx.WorkspaceId, special, graph.ProducerId, null, CancellationToken.None);

        var pullOnlyByRepository = Calls(ctx, "CommitSyncRepository")
            .Select(call => (Name: call.GetProperty("repositoryName").GetString()!, PullOnly: call.GetProperty("pullOnly").GetBoolean()))
            .OrderBy(call => call.Name, StringComparer.Ordinal)
            .ToList();
        var expected = new[] { (Name: ConsumerName, PullOnly: false), (Name: ProducerName, PullOnly: true), (Name: ProducerName, PullOnly: true) }
            .OrderBy(call => call.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, pullOnlyByRepository);
    }

    [Fact]
    public async Task A_pull_of_the_Workspace_repository_is_never_pull_only()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>()
            .PullAsync(ctx.WorkspaceId, special, graph.ProducerId, null, CancellationToken.None);

        var call = Assert.Single(Calls(ctx, "CommitSyncRepository"));
        Assert.False(call.GetProperty("pullOnly").GetBoolean());
    }

    [Fact]
    public async Task A_Git_Changes_commit_in_a_read_only_repository_is_refused_before_the_Worker()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        var refused = await operations.CommitAsync(ctx.WorkspaceId, special, graph.ProducerId, "message", stageAllFirst: true, CancellationToken.None);
        await operations.CommitAsync(ctx.WorkspaceId, special, graph.ConsumerId, "message", stageAllFirst: true, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Equal(RepositoryOperationPolicy.ReferenceMessage, refused.ErrorMessage);
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "CommitGitChanges"));
    }

    [Fact]
    public async Task Staging_and_unstaging_in_a_read_only_repository_stay_possible()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        await operations.StageAsync(ctx.WorkspaceId, special, graph.ProducerId, GitChangeOperationScope.Repository, [], CancellationToken.None);
        await operations.UnstageAsync(ctx.WorkspaceId, special, graph.ProducerId, GitChangeOperationScope.Repository, [], CancellationToken.None);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "StageGitChanges"));
        Assert.Equal([ProducerName], RepositoryNames(ctx, "UnstageGitChanges"));
    }

    [Fact]
    public async Task The_Workspace_repository_is_committed_even_when_it_carries_the_flag()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>()
            .CommitAsync(ctx.WorkspaceId, special, graph.ProducerId, "message", stageAllFirst: true, CancellationToken.None);

        Assert.Equal([ProducerName], RepositoryNames(ctx, "CommitGitChanges"));
    }

    [Fact]
    public async Task A_Git_Changes_commit_in_a_read_only_snapshot_of_a_Feature_is_refused()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId, Branch: null), new FeatureRepo(graph.ConsumerId)]);
        await MarkReferenceSnapshotAsync(ctx, feature, graph.ProducerId);

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        var refused = await operations.CommitAsync(ctx.WorkspaceId, feature, graph.ProducerId, "message", stageAllFirst: true, CancellationToken.None);
        await operations.CommitAsync(ctx.WorkspaceId, feature, graph.ConsumerId, "message", stageAllFirst: true, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Equal(RepositoryOperationPolicy.ReferenceSnapshotMessage, refused.ErrorMessage);
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "CommitGitChanges"));
    }

    [Fact]
    public async Task Creating_a_branch_in_all_repositories_leaves_the_read_only_one_out_and_names_it()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>()
            .CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, special, "topic", "__default__", repositoryIds: null);

        var skip = Assert.Single(outcome.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.Reference, skip.Reason);
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "CreateBranch"));
    }

    [Fact]
    public async Task Creating_a_branch_in_only_read_only_repositories_does_nothing()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>()
            .CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, special, "topic", "__default__", repositoryIds: new HashSet<int> { graph.ProducerId });

        Assert.Equal([graph.ProducerId], outcome.Skipped.Select(skip => skip.RepositoryId));
        Assert.Empty(Calls(ctx, "CreateBranch"));
    }

    [Fact]
    public async Task A_Workspace_wide_checkout_leaves_the_read_only_repository_where_it_is()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var producerLink = await ReadLinkAsync(ctx, graph.ProducerId);
        var consumerLink = await ReadLinkAsync(ctx, graph.ConsumerId);

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>().CheckoutBranchForWorkspaceAsync(
            ctx.WorkspaceId,
            special,
            new Dictionary<int, int>
            {
                [graph.ProducerId] = producerLink.WorkspaceRepositoryId,
                [graph.ConsumerId] = consumerLink.WorkspaceRepositoryId,
            },
            "main",
            reportProgress: null,
            CancellationToken.None);

        Assert.Equal([graph.ProducerId], result.Skipped.Select(skip => skip.RepositoryId));
        Assert.Equal([ConsumerName], RepositoryNames(ctx, "CheckoutBranch"));
    }

    [Fact]
    public async Task Pull_requests_of_a_read_only_repository_are_not_created_or_merged()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);

        await using var scope = ctx.CreateScope();
        // The GitHub services are never reached: every request here is for the read-only repository.
        var operations = new WorkspacePullRequestOperations(
            null!,
            scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryOperationPolicy>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>());

        var created = await operations.CreateAsync(
            ctx.WorkspaceId,
            [
                new CreatePullRequestRequest
                {
                    RepositoryId = graph.ProducerId,
                    Owner = "acme",
                    RepositoryName = ProducerName,
                    HeadBranch = "feature/x",
                    BaseBranch = "main",
                    Title = "title",
                },
            ],
            progress: null,
            CancellationToken.None);
        var merged = await operations.MergeAsync(ctx.WorkspaceId, graph.ProducerId, 12, MergeMethod.Squash, expectedHeadSha: null, CancellationToken.None);

        var result = Assert.Single(created);
        Assert.False(result.Success);
        Assert.Equal(RepositoryOperationPolicy.ReferenceMessage, result.ErrorMessage);
        Assert.False(merged.Success);
        Assert.Equal(RepositoryOperationPolicy.ReferenceMessage, merged.Message);
    }

    [Fact]
    public async Task A_Feature_refuses_every_checkout_in_a_read_only_snapshot()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId, Branch: null), new FeatureRepo(graph.ConsumerId)]);
        await MarkReferenceSnapshotAsync(ctx, feature, graph.ProducerId);
        var guard = ctx.Resolve<IFeatureBranchGuard>();

        Assert.Equal(
            FeatureBranchPolicy.ReferenceSnapshotMessage,
            await guard.CheckAsync(feature, graph.ProducerId, FeatureBranchAction.Checkout, "feature-y", isTag: false));
        Assert.Equal(
            FeatureBranchPolicy.ReferenceSnapshotMessage,
            await guard.CheckAsync(feature, graph.ProducerId, FeatureBranchAction.Checkout, "v1.0.0", isTag: true));
        Assert.Null(await guard.CheckAsync(feature, graph.ConsumerId, FeatureBranchAction.Checkout, "feature-y", isTag: false));
    }

    [Fact]
    public async Task A_state_write_keeps_a_read_only_snapshot_without_a_branch_and_in_sync()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId, Branch: null, Outgoing: 0, HasUpstream: null)]);
        await MarkReferenceSnapshotAsync(ctx, feature, graph.ProducerId);

        await using (var scope = ctx.CreateScope())
        {
            // A probe of a detached HEAD reports a description in place of a branch name.
            await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>().ApplyAsync(
                feature,
                ctx.WorkspaceId,
                graph.ProducerId,
                new RepositoryStateSnapshot
                {
                    BranchName = "HEAD",
                    HeadCommit = "abc123",
                    IdentityProbed = true,
                    CommitCountsProbed = true,
                    OutgoingCommits = 4,
                },
                new RepositoryStateWriteOptions { SyncStatus = SyncStatusWrite.Derive });
        }

        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.AsNoTracking().SingleAsync(l => l.RepositoryId == graph.ProducerId);
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s =>
            s.WorkspaceFeatureContextId == feature.Value && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId);

        Assert.Null(state.BranchName);
        Assert.Null(state.CheckedOutTag);
        Assert.Equal("abc123", state.HeadCommit);
        Assert.Equal(RepoSyncStatus.InSync, state.SyncStatus);
        Assert.NotEqual(4, state.OutgoingCommits);
        // A Feature write never reaches the shared link.
        Assert.Equal("feature/x", link.BranchName);
    }

    [Fact]
    public void A_snapshot_has_no_expected_branch_and_is_never_off_its_Feature_branch()
    {
        Assert.Equal("feat", FeatureBranchPolicy.ExpectedBranch("feat", pinnedTag: null, isReferenceSnapshot: false));
        Assert.Null(FeatureBranchPolicy.ExpectedBranch("feat", pinnedTag: null, isReferenceSnapshot: true));
        Assert.Null(FeatureBranchPolicy.GetOffFeatureBranch("feat", pinnedTag: null, currentBranch: null, hasRecordedState: true, isReferenceSnapshot: true));
        Assert.Equal("feat", FeatureBranchPolicy.GetOffFeatureBranch("feat", pinnedTag: null, currentBranch: null, hasRecordedState: true));
    }

    [Fact]
    public void Removing_a_Feature_treats_an_unchanged_snapshot_as_nothing_to_lose()
    {
        var unchanged = new RemoveFeatureRepositoryPlan
        {
            IsReferenceSnapshot = true,
            BaseCommitSha = "ABC123",
            HeadCommit = "abc123",
            // The Source was on a branch ahead of the default branch: that is not work of this Feature.
            AheadOfDefault = 7,
        };
        var moved = new RemoveFeatureRepositoryPlan { IsReferenceSnapshot = true, BaseCommitSha = "abc123", HeadCommit = "def456" };
        var unknown = new RemoveFeatureRepositoryPlan { IsReferenceSnapshot = true, BaseCommitSha = "abc123", HeadCommit = null };

        Assert.True(unchanged.IsUnchangedReferenceSnapshot);
        Assert.False(unchanged.NeedsForceDeleteAuthorization(pullRequestStatusUnknown: true));
        Assert.False(moved.IsUnchangedReferenceSnapshot);
        Assert.False(unknown.IsUnchangedReferenceSnapshot);
    }

    [Fact]
    public void A_workspace_commit_names_the_read_only_repository_it_leaves_alone()
    {
        var writable = View(1, changed: 2);
        var reference = View(2, changed: 3) with { IsReference = true };

        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.All, [writable], [writable, reference]);

        Assert.Equal([1], plan.Targets.Select(target => target.WorkspaceRepositoryId));
        var (repository, reason) = Assert.Single(plan.LeftOut);
        Assert.Equal(2, repository.WorkspaceRepositoryId);
        Assert.Equal(WorkspaceCommitTargetPlanner.ReferenceReason, reason);
        Assert.Empty(plan.AddedSinceClick);
        Assert.False(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.All, reference));
        Assert.True(WorkspaceCommitModeHelper.IsRepositoryTarget(WorkspaceCommitMode.All, writable));
    }

    private static WorkspaceGitChangesRepositoryView View(int id, int changed) =>
        new()
        {
            WorkspaceRepositoryId = id,
            RepositoryId = id + 100,
            RepositoryName = "repo-" + id,
            BranchName = "feature/x",
            DefaultBranchName = "main",
            ChangedCount = changed,
            Changes = [],
        };

    [Fact]
    public async Task Return_to_Default_of_one_read_only_repository_is_a_plain_checkout_of_the_default_branch()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        ctx.WorkerBridge.Respond("CheckoutBranch", new { success = true, currentBranch = "main" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>().ReturnToDefaultAsync(
            ctx.WorkspaceId, special, graph.ProducerId, "feature/x", deleteRemoteBranch: true, allowForceDeleteLocalBranch: true);

        Assert.True(outcome.IsSuccessStatus, outcome.ErrorText);
        var checkout = Assert.Single(Calls(ctx, "CheckoutBranch"));
        Assert.Equal("main", checkout.GetProperty("branchName").GetString());
        // Asked to delete the remote branch and to force: neither happens, no branch is deleted anywhere.
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
        Assert.Empty(Calls(ctx, "DeleteBranch"));
    }

    [Fact]
    public async Task Return_to_Default_of_several_repositories_leaves_the_read_only_one_out_and_names_it()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedReferenceProducerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>().ExecuteReturnToDefaultAsync(
            ctx.WorkspaceId,
            special,
            [graph.ProducerId],
            new ReturnToDefaultOptions(DeleteRemoteBranch: true, AllowForceDeleteLocalBranch: true, CloseOpenPullRequest: false),
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.Reference, skip.Reason);
        Assert.Empty(Calls(ctx, "ReturnToDefaultBranch"));
        Assert.Empty(Calls(ctx, "CheckoutBranch"));
    }

    [Fact]
    public async Task The_projects_of_a_read_only_repository_are_still_scanned_but_it_is_never_an_update_target()
    {
        await using var ctx = await CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var updates = scope.ServiceProvider.GetRequiredService<IWorkspaceUpdateOperations>();
        var (before, _) = await updates.GetUpdatePlanAsync(ctx.WorkspaceId, special);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.IsReference = true);
        var (after, _) = await updates.GetUpdatePlanAsync(ctx.WorkspaceId, special);
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var synced = await git.SyncDependenciesAsync(ctx.WorkspaceId, special);
        await git.RefreshWorkspaceProjectsAsync(ctx.WorkspaceId, special);

        Assert.Equal([graph.ConsumerId], before.Select(p => p.RepoId));
        Assert.Empty(after);
        Assert.Empty(synced);
        Assert.Empty(Calls(ctx, "SyncRepositoryDependencies"));
        Assert.Equal(
            new[] { ConsumerName, ProducerName }.Order(),
            RepositoryNames(ctx, "RefreshRepositoryProjects").Order());
    }

    /// <summary>The two-repository graph with the producer read-only. Both keep their unpushed commits.</summary>
    private static async Task<Graph> SeedReferenceProducerAsync(SyncStateTestContext ctx)
    {
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link =>
        {
            link.IsReference = true;
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 2;
            link.BranchHasUpstream = true;
        });
        return graph;
    }

    /// <summary>Turns a seeded Feature repository into the detached snapshot a Feature creates for a read-only Source.</summary>
    private static async Task MarkReferenceSnapshotAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, int repositoryId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var row = await db.WorkspaceFeatureRepositories.SingleAsync(r =>
            r.WorkspaceFeatureContextId == contextId.Value && r.WorkspaceRepository!.RepositoryId == repositoryId);
        row.IsReferenceSnapshot = true;
        row.SourceBranchName = row.ParentBranchName;
        row.ParentBranchName = null;
        await db.SaveChangesAsync();
    }
}
