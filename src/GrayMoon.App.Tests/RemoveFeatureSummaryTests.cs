using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Remove Feature dialog opens from stored facts: no Worker command and no pull request refresh run before the
/// user can confirm. What it says is worded as a record, never as the present state of a checkout.
/// </summary>
public sealed class RemoveFeatureSummaryTests
{
    [Fact]
    public async Task The_summary_is_read_from_the_database_without_any_Worker_command()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureAsync(ctx, repositories: 3, pushed: 2, recordedChanged: 1, tagPinned: 1);

        await using var scope = ctx.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
            .GetRemoveFeatureSummaryAsync(featureContextId);

        Assert.True(summary.Success, summary.Error);
        Assert.Equal("feat-summary", summary.FeatureName);
        Assert.Equal(3, summary.RepositoryCount);
        Assert.Equal(0, summary.AlreadyRemovedCount);
        Assert.Equal(2, summary.FeatureBranchCount);
        Assert.Equal(2, summary.RecordedPushedBranchCount);
        Assert.Equal(1, summary.RecordedChangedRepositoryCount);
        Assert.Empty(summary.Pending);
        // Opening the dialog costs no Worker command at all: no InspectWorktree, no status scan.
        Assert.Equal(0, WorkerCommandCounts.Of(ctx.WorkerBridge).Total);
    }

    [Fact]
    public async Task Confirming_from_the_summary_removes_without_a_single_inspection()
    {
        // Open and confirm, as the dialog does it: the summary, then the removal with no reviewed details.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureAsync(ctx, repositories: 3, pushed: 0, recordedChanged: 0, tagPinned: 0);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true, branchSha = "abc123", hasUpstream = false });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CleanupFeatureFolder, new { outcome = "Removed", remainingFileCount = 0 });

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var summary = await operations.GetRemoveFeatureSummaryAsync(featureContextId);
        var result = await operations.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions
        {
            DeleteLocalBranches = RemoveFeatureQuickDialog.ShowDeleteLocalBranches(summary),
            DeleteRemoteBranches = RemoveFeatureQuickDialog.ShowDeleteRemoteBranches(summary),
        });

        Assert.True(result.Success, result.Error);
        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertNone(WorkerHubMethods.InspectWorktree, "GetGitChangeStatus", "SyncRepository");
        // One removal and one local branch delete per repository. The analysis the dialog used to run first sent
        // one InspectWorktree per repository (five git processes each) before the user could confirm.
        counts.AssertCount(WorkerHubMethods.RemoveGitWorktree, 3);
        counts.AssertCount("DeleteBranch", 3);
    }

    [Fact]
    public async Task An_incomplete_removal_is_listed_with_the_reason_each_repository_recorded()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureAsync(ctx, repositories: 3, pushed: 0, recordedChanged: 0, tagPinned: 0);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, args =>
            System.Text.Json.JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString()!.EndsWith("repo-1", StringComparison.Ordinal)
                ? new WorkerCommandResponse(
                    false, new { success = false, errorMessage = "The worktree is locked. Lock reason: build.", failureKind = "WorktreeLocked" }, "locked")
                : new WorkerCommandResponse(true, new { success = true }, null));
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true, branchSha = "abc123", hasUpstream = false });

        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        Assert.False((await operations.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions())).Success);
        var before = WorkerCommandCounts.Of(ctx.WorkerBridge).Total;

        var summary = await operations.GetRemoveFeatureSummaryAsync(featureContextId);

        Assert.Equal(before, WorkerCommandCounts.Of(ctx.WorkerBridge).Total);
        Assert.Equal(1, summary.RepositoryCount);
        Assert.Equal(2, summary.AlreadyRemovedCount);
        var pending = Assert.Single(summary.Pending);
        Assert.Equal("repo-1", pending.RepositoryName);
        Assert.False(pending.WorktreeRemoved);
        Assert.Equal(OperationReasonCodes.WorktreeLocked, pending.ReasonCode);
        Assert.Equal(OperationAuthorizations.UnlockWorktree, pending.RequiredAuthorization);
        Assert.Contains("locked", pending.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 repositories were already removed", RemoveFeatureQuickDialog.Headline(summary), StringComparison.Ordinal);

        // The same reason is what Feature Status shows for the repository.
        var status = await operations.GetFeatureStatusAsync(featureContextId);
        Assert.Contains(status!.Repositories, r => r.RepositoryName == "repo-1" && r.LastError!.Contains("locked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_special_Workspace_has_no_removal_summary()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>().GetRemoveFeatureSummaryAsync(special);

        Assert.False(summary.Success);
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));
    }

    [Fact]
    public void The_dialog_text_names_recorded_changes_as_a_record_and_never_calls_a_Feature_clean()
    {
        var none = new RemoveFeatureSummary { Success = true, RepositoryCount = 40, FeatureBranchCount = 40 };
        var some = new RemoveFeatureSummary { Success = true, RepositoryCount = 40, RecordedChangedRepositoryCount = 2, FeatureBranchCount = 38, RecordedPushedBranchCount = 5 };

        Assert.Equal("This removes the Feature's folders of 40 repositories from your computer.", RemoveFeatureQuickDialog.Headline(none));
        // Nothing recorded is not "clean": no line at all.
        Assert.Null(RemoveFeatureQuickDialog.RecordedChangesText(none));
        Assert.Contains("when GrayMoon last looked", RemoveFeatureQuickDialog.RecordedChangesText(some), StringComparison.Ordinal);
        Assert.Contains("kept", RemoveFeatureQuickDialog.SafetyText, StringComparison.Ordinal);
        Assert.Contains("Nothing is discarded", RemoveFeatureQuickDialog.SafetyText, StringComparison.Ordinal);
        Assert.DoesNotContain("safe to remove", RemoveFeatureQuickDialog.SafetyText + RemoveFeatureQuickDialog.BranchNote, StringComparison.OrdinalIgnoreCase);
        Assert.True(RemoveFeatureQuickDialog.ShowDeleteLocalBranches(none));
        Assert.False(RemoveFeatureQuickDialog.ShowDeleteRemoteBranches(none));
        Assert.True(RemoveFeatureQuickDialog.ShowDeleteRemoteBranches(some));
    }

    [Theory]
    [InlineData(1, 0, "This removes the Feature's folder of 1 repository from your computer.")]
    [InlineData(3, 1, "This removes the Feature's folders of 3 repositories from your computer. 1 repository was already removed.")]
    [InlineData(0, 4, "Every folder of this Feature is already removed. What is left is listed below.")]
    public void The_headline_counts_what_is_left_and_what_is_gone(int left, int removed, string expected)
        => Assert.Equal(expected, RemoveFeatureQuickDialog.Headline(new RemoveFeatureSummary { RepositoryCount = left, AlreadyRemovedCount = removed }));

    /// <summary>
    /// A Feature "feat-summary" over <paramref name="repositories"/> new repositories named repo-0, repo-1, ...; the
    /// first <paramref name="tagPinned"/> are tag-pinned, the first <paramref name="pushed"/> branch repositories
    /// are recorded as pushed, and the first <paramref name="recordedChanged"/> have a recorded Git Changes status.
    /// </summary>
    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(
        SyncStateTestContext ctx, int repositories, int pushed, int recordedChanged, int tagPinned)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.WorkspaceRepositories.RemoveRange(await db.WorkspaceRepositories.ToListAsync());
        await db.SaveChangesAsync();
        var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-summary",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();
        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        var pushedLeft = pushed;
        for (var i = 0; i < repositories; i++)
        {
            var repository = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = $"repo-{i}",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/repo-{i}.git",
            };
            db.Repositories.Add(repository);
            await db.SaveChangesAsync();
            var link = new WorkspaceRepositoryLink { WorkspaceId = ctx.WorkspaceId, RepositoryId = repository.RepositoryId, DefaultBranchName = "main" };
            db.WorkspaceRepositories.Add(link);
            await db.SaveChangesAsync();
            var pinned = i < tagPinned;
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\feat-summary\repo-{i}",
                State = WorkspaceFeatureRepositoryState.Ready,
                BaseCommitSha = "abc123",
                PinnedTag = pinned ? "v1.0.0" : null,
                CreatedAt = DateTime.UtcNow,
            });
            var isPushed = !pinned && pushedLeft-- > 0;
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                BranchName = pinned ? null : "feat-summary",
                BranchHasUpstream = pinned ? null : isPushed,
                SyncStatus = RepoSyncStatus.InSync,
            });
            if (i < recordedChanged)
            {
                db.WorkspaceGitContextRepositoryStatuses.Add(new WorkspaceGitContextRepositoryStatus
                {
                    WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    ChangedCount = 2,
                });
            }
        }

        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
