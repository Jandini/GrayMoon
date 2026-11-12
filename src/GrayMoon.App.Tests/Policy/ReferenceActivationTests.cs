using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Policy;
using GrayMoon.App.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The activation check: may a repository become read-only? The rule is judged on one InspectWorktree answer; the
/// checker asks the Worker about the Workspace checkout and every Feature worktree. The Worker is a fake here.
/// </summary>
public sealed class ReferenceActivationTests
{
    private static InspectWorktreeWorkerResponse Clean() => new()
    {
        Exists = true,
        Branch = "main",
        HeadSha = "1111111111111111111111111111111111111111",
        IsDirty = false,
        HasUpstream = true,
        AheadOfUpstream = 0,
    };

    [Fact]
    public void A_clean_branch_level_with_its_upstream_passes_without_a_message()
    {
        Assert.Equal((ReferenceActivationSeverity.Pass, null), ReferenceActivationRule.Evaluate(Clean()));
    }

    [Fact]
    public void No_answer_from_the_Worker_blocks()
    {
        Assert.Equal((ReferenceActivationSeverity.Block, ReferenceActivationRule.UnreadableMessage), ReferenceActivationRule.Evaluate(null));
    }

    [Fact]
    public void An_error_or_an_unknown_dirty_state_blocks()
    {
        var withError = Clean();
        withError.Error = "status failed";
        var unknownDirty = Clean();
        unknownDirty.IsDirty = null;

        Assert.Equal(ReferenceActivationSeverity.Block, ReferenceActivationRule.Evaluate(withError).Severity);
        Assert.Equal((ReferenceActivationSeverity.Block, ReferenceActivationRule.UnreadableMessage), ReferenceActivationRule.Evaluate(unknownDirty));
    }

    [Fact]
    public void A_tag_or_detached_HEAD_passes_whatever_the_upstream_fields_say()
    {
        var detached = Clean();
        detached.Branch = null;
        detached.HasUpstream = null;
        detached.AheadOfUpstream = null;

        Assert.Equal(ReferenceActivationSeverity.Pass, ReferenceActivationRule.Evaluate(detached).Severity);
    }

    [Fact]
    public void A_branch_whose_upstream_state_is_unknown_blocks()
    {
        var unknown = Clean();
        unknown.HasUpstream = null;

        Assert.Equal((ReferenceActivationSeverity.Block, ReferenceActivationRule.UnreadableMessage), ReferenceActivationRule.Evaluate(unknown));
    }

    [Theory]
    [InlineData(true, ReferenceActivationSeverity.Pass)]
    [InlineData(false, ReferenceActivationSeverity.Block)]
    [InlineData(null, ReferenceActivationSeverity.Block)]
    public void A_branch_without_upstream_passes_only_when_its_commit_is_on_the_remote(bool? headOnRemote, ReferenceActivationSeverity expected)
    {
        var noUpstream = Clean();
        noUpstream.HasUpstream = false;
        noUpstream.AheadOfUpstream = null;
        noUpstream.HeadOnRemote = headOnRemote;

        var (severity, message) = ReferenceActivationRule.Evaluate(noUpstream);

        Assert.Equal(expected, severity);
        Assert.Equal(expected == ReferenceActivationSeverity.Block ? ReferenceActivationRule.NoUpstreamMessage : null, message);
    }

    [Theory]
    [InlineData(1, "it has 1 unpushed commit.")]
    [InlineData(3, "it has 3 unpushed commits.")]
    public void Unpushed_commits_block_and_are_counted(int ahead, string expected)
    {
        var unpushed = Clean();
        unpushed.AheadOfUpstream = ahead;

        Assert.Equal((ReferenceActivationSeverity.Block, expected), ReferenceActivationRule.Evaluate(unpushed));
    }

    [Fact]
    public void An_unknown_ahead_count_blocks()
    {
        var unknown = Clean();
        unknown.AheadOfUpstream = null;

        Assert.Equal(ReferenceActivationSeverity.Block, ReferenceActivationRule.Evaluate(unknown).Severity);
    }

    [Fact]
    public void Uncommitted_changes_are_a_warning_and_never_hide_a_block()
    {
        var dirty = Clean();
        dirty.IsDirty = true;
        var dirtyAndAhead = Clean();
        dirtyAndAhead.IsDirty = true;
        dirtyAndAhead.AheadOfUpstream = 2;
        var dirtyDetached = Clean();
        dirtyDetached.IsDirty = true;
        dirtyDetached.Branch = null;

        Assert.Equal((ReferenceActivationSeverity.Warning, ReferenceActivationRule.DirtyMessage), ReferenceActivationRule.Evaluate(dirty));
        Assert.Equal(ReferenceActivationSeverity.Block, ReferenceActivationRule.Evaluate(dirtyAndAhead).Severity);
        Assert.Equal(ReferenceActivationSeverity.Warning, ReferenceActivationRule.Evaluate(dirtyDetached).Severity);
    }

    [Fact]
    public async Task A_clean_repository_has_no_findings_and_its_HEAD_is_returned()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Clean());

        var report = await CheckAsync(ctx, graph.ProducerId);

        Assert.Empty(report.Findings);
        Assert.False(report.IsBlocked);
        Assert.Equal(new ReferenceActivationHead("main", Clean().HeadSha), report.Heads[graph.ProducerId]);
        Assert.Single(InspectedPaths(ctx));
    }

    [Fact]
    public async Task Unpushed_commits_in_the_Workspace_checkout_block_and_name_the_repository()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ =>
        {
            var response = Clean();
            response.AheadOfUpstream = 2;
            return response;
        });

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.True(report.IsBlocked);
        Assert.Equal(graph.ProducerId, finding.RepositoryId);
        Assert.Null(finding.FeatureName);
        Assert.Equal($"Repository {ProducerName}: it has 2 unpushed commits.", finding.Message);
        Assert.Empty(report.Heads);
    }

    [Fact]
    public async Task Unpushed_commits_in_a_Feature_worktree_block_although_the_Workspace_checkout_is_clean()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        RespondInspect(ctx, path =>
        {
            var response = Clean();
            if (path.Contains("feature-y", StringComparison.Ordinal))
            {
                response.Branch = "feature/y";
                response.AheadOfUpstream = 1;
            }
            return response;
        });

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.True(finding.Blocks);
        Assert.Equal("feature-y", finding.FeatureName);
        Assert.Equal($"Repository {ProducerName} in Feature feature-y: it has 1 unpushed commit.", finding.Message);
        Assert.Equal(2, InspectedPaths(ctx).Count);
        // The Workspace checkout itself was fine, so its HEAD is still reported.
        Assert.True(report.Heads.ContainsKey(graph.ProducerId));
    }

    [Fact]
    public async Task A_Feature_worktree_that_is_not_ready_is_a_warning_and_is_not_inspected()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        await SetFeatureRowStateAsync(ctx, WorkspaceFeatureRepositoryState.NeedsRepair);
        RespondInspect(ctx, _ => Clean());

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReferenceActivationSeverity.Warning, finding.Severity);
        Assert.Equal("feature-y", finding.FeatureName);
        Assert.False(report.IsBlocked);
        Assert.Single(InspectedPaths(ctx));
    }

    [Fact]
    public async Task A_removed_Feature_worktree_is_ignored()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        await SetFeatureRowStateAsync(ctx, WorkspaceFeatureRepositoryState.Removed);
        RespondInspect(ctx, _ => Clean());

        var report = await CheckAsync(ctx, graph.ProducerId);

        Assert.Empty(report.Findings);
        Assert.Single(InspectedPaths(ctx));
    }

    [Fact]
    public async Task A_missing_Feature_folder_is_a_warning()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        RespondInspect(ctx, path => path.Contains("feature-y", StringComparison.Ordinal)
            ? new InspectWorktreeWorkerResponse { Exists = false }
            : Clean());

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReferenceActivationSeverity.Warning, finding.Severity);
        Assert.Equal($"Repository {ProducerName} in Feature feature-y: {ReferenceActivationRule.MissingFolderMessage}", finding.Message);
    }

    [Fact]
    public async Task A_Feature_folder_that_exists_but_cannot_be_read_blocks()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        RespondInspect(ctx, path => path.Contains("feature-y", StringComparison.Ordinal)
            ? new InspectWorktreeWorkerResponse { Exists = true, Error = "git status failed" }
            : Clean());

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.True(finding.Blocks);
        Assert.Equal("feature-y", finding.FeatureName);
    }

    [Fact]
    public async Task A_repository_that_is_not_cloned_on_this_machine_is_a_warning()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SeedFeatureAsync(ctx, [new FeatureRepo(graph.ProducerId)]);
        RespondInspect(ctx, _ => new InspectWorktreeWorkerResponse { Exists = false, Error = ReferenceActivationChecker.MainCheckoutMissingError });

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReferenceActivationSeverity.Warning, finding.Severity);
        Assert.Null(finding.FeatureName);
        Assert.Empty(report.Heads);
        // Without the clone there are no worktrees to ask about.
        Assert.Single(InspectedPaths(ctx));
    }

    [Fact]
    public async Task A_Worker_that_fails_the_command_blocks()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);

        var report = await CheckAsync(ctx, graph.ProducerId);

        var finding = Assert.Single(report.Findings);
        Assert.True(finding.Blocks);
        Assert.Equal($"Repository {ProducerName}: {ReferenceActivationRule.UnreadableMessage}", finding.Message);
    }

    [Fact]
    public async Task Every_repository_is_reported_and_the_Workspace_repository_is_left_out()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        RespondInspect(ctx, _ =>
        {
            var response = Clean();
            response.AheadOfUpstream = 1;
            return response;
        });

        var report = await CheckAsync(ctx, graph.ProducerId, graph.ConsumerId);

        Assert.Equal([graph.ProducerId], report.Findings.Select(finding => finding.RepositoryId));
        Assert.Single(InspectedPaths(ctx));
    }

    [Fact]
    public async Task One_blocked_repository_does_not_stop_the_check_of_the_others()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, path =>
        {
            var response = Clean();
            if (path.Contains(ProducerName, StringComparison.Ordinal))
                response.AheadOfUpstream = 1;
            else
                response.IsDirty = true;
            return response;
        });

        var report = await CheckAsync(ctx, graph.ProducerId, graph.ConsumerId);

        Assert.Equal([graph.ProducerId], report.Blocks.Select(finding => finding.RepositoryId));
        Assert.Equal([graph.ConsumerId], report.Warnings.Select(finding => finding.RepositoryId));
        Assert.Equal([graph.ConsumerId], report.Heads.Keys);
    }

    private static async Task<ReferenceActivationReport> CheckAsync(SyncStateTestContext ctx, params int[] repositoryIds)
    {
        await using var scope = ctx.CreateScope();
        // The test container registers its services by hand; the checker is built from them.
        return await ActivatorUtilities.CreateInstance<ReferenceActivationChecker>(scope.ServiceProvider).CheckAsync(ctx.WorkspaceId, repositoryIds);
    }

    private static void RespondInspect(SyncStateTestContext ctx, Func<string, InspectWorktreeWorkerResponse> answer) =>
        ctx.WorkerBridge.Respond("InspectWorktree", args =>
            new WorkerCommandResponse(true, answer(JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString()!), null));

    private static List<string> InspectedPaths(SyncStateTestContext ctx) =>
        [.. ctx.WorkerBridge.Calls
            .Where(call => call.Command == "InspectWorktree")
            .Select(call => JsonSerializer.SerializeToElement(call.Args).GetProperty("worktreePath").GetString()!)];

    private static async Task SetFeatureRowStateAsync(SyncStateTestContext ctx, WorkspaceFeatureRepositoryState state)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var row = await db.WorkspaceFeatureRepositories.SingleAsync();
        row.State = state;
        await db.SaveChangesAsync();
    }
}
