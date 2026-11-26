using System.Collections.Concurrent;
using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Remove Feature, safe first: every repository on its own, nothing forced without a reviewed consent for that
/// repository, each step with its own stored outcome, and a retry that runs only what is not resolved. The Worker
/// is a stand-in that answers per repository; what the real Worker and real git do is held by
/// <c>WorktreeRemovalSafetyTests</c> in the Worker tests.
/// </summary>
public sealed class RemoveFeatureSafeFirstTests
{
    private const string FeatureName = "feat-safe";
    private const string Root = "workspace-root";

    [Fact]
    public async Task Forty_repositories_with_two_blocked_sources_report_37_removed_2_blocked_and_the_root_waiting()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 39, withRoot: true);
        s.Worktree["source-07"] = Scenario.Refused("UncommittedChanges", "fatal: contains modified or untracked files");
        s.Worktree["source-23"] = Scenario.Refused("WorktreeLocked", "The worktree is locked. Lock reason: build.");

        var result = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.False(result.Success);
        var outcome = result.Outcome!;
        Assert.Equal(OperationOutcomeStatus.Partial, outcome.Status);
        Assert.Equal(40, outcome.Targets.Count);
        var byStatus = outcome.CountByStatus();
        Assert.Equal(37, byStatus[OperationOutcomeStatus.Succeeded]);
        Assert.Equal(3, byStatus[OperationOutcomeStatus.Blocked]);
        Assert.Equal(OperationReasonCodes.UncommittedChanges, s.Step(outcome, "source-07", RemoveFeatureSteps.Worktree).ReasonCode);
        Assert.Equal(OperationAuthorizations.DiscardUncommittedChanges, s.Step(outcome, "source-07", RemoveFeatureSteps.Worktree).RequiredAuthorization);
        Assert.Equal(OperationReasonCodes.WorktreeLocked, s.Step(outcome, "source-23", RemoveFeatureSteps.Worktree).ReasonCode);
        Assert.Equal(OperationAuthorizations.UnlockWorktree, s.Step(outcome, "source-23", RemoveFeatureSteps.Worktree).RequiredAuthorization);
        Assert.Equal(OperationReasonCodes.WaitingForOtherTargets, s.Step(outcome, Root, RemoveFeatureSteps.Worktree).ReasonCode);

        // The 37 are reported by name, also in a result that is not a success.
        Assert.Equal(37, result.RemoveFeatureReport!.Count);
        Assert.Contains("37 of 40 repositories removed", result.Error, StringComparison.Ordinal);
        Assert.Contains("source-07", result.Error, StringComparison.Ordinal);
        Assert.Contains("source-23", result.Error, StringComparison.Ordinal);
        Assert.Contains(WorkspaceFeatureOperations.RootKeptMessage, result.Error, StringComparison.Ordinal);

        // No live preflight, no force, no unlock, and the root was never asked while repositories are inside it.
        var counts = WorkerCommandCounts.Of(s.Ctx.WorkerBridge);
        counts.AssertNone(WorkerHubMethods.InspectWorktree, WorkerHubMethods.CleanupFeatureFolder, "SyncRepository");
        counts.AssertCount(WorkerHubMethods.RemoveGitWorktree, 39);
        Assert.All(counts.ArgsOf(WorkerHubMethods.RemoveGitWorktree), args =>
        {
            var json = JsonSerializer.SerializeToElement(args);
            Assert.False(json.GetProperty("force").GetBoolean());
            Assert.False(json.GetProperty("unlock").GetBoolean());
            Assert.DoesNotContain(Root, json.GetProperty("worktreePath").GetString(), StringComparison.Ordinal);
        });

        // Durable: the Feature and its rows say the same after the run, from the database alone.
        var rows = await s.ReadRowsAsync();
        Assert.Equal(37, rows.Count(r => r.State == WorkspaceFeatureRepositoryState.Removed));
        Assert.Equal(3, rows.Count(r => r.State == WorkspaceFeatureRepositoryState.Removing));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.RemovalOutcomeJson)));
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, await s.ReadFeatureStateAsync());
    }

    [Fact]
    public async Task A_retry_asks_the_Worker_only_for_what_is_not_removed_and_then_finishes_the_Feature()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 5, withRoot: true);
        s.Worktree["source-02"] = Scenario.Refused("UncommittedChanges", "fatal: contains modified or untracked files");
        var first = await s.RemoveAsync(new RemoveFeatureOptions());
        Assert.False(first.Success);
        var before = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).Total;

        // The user committed the work; the same safe request is sent again.
        s.Worktree.Remove("source-02", out _);
        var second = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.True(second.Success, second.Error);
        var retry = WorkerCommandCounts.Since(s.Ctx.WorkerBridge, before);
        // The source first, the root after it.
        Assert.Equal(
            new[] { s.WorktreePath("source-02"), s.WorktreePath(Root) },
            retry.ArgValues(WorkerHubMethods.RemoveGitWorktree, "worktreePath").ToArray());
        // The report still names all six, and the Feature is gone.
        Assert.Equal(6, second.RemoveFeatureReport!.Count);
        Assert.Equal(OperationOutcomeStatus.Succeeded, second.Outcome!.Status);
        Assert.Empty(await s.ReadRowsAsync());
    }

    [Fact]
    public async Task One_blocked_repository_does_not_stop_the_others_and_nothing_is_retried_with_force()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 4, withRoot: false);
        s.Worktree["source-01"] = Scenario.Refused("UnpreservedCommits", "The checked-out commit 1234567890 is not on any branch or tag.");
        s.Worktree["source-03"] = Scenario.Refused("NestedRepository", "The folder contains another Git repository at 'tmp/x'.");

        var result = await s.RemoveAsync(new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowUnlockWorktrees = true, AllowForceDeleteLocalBranches = true });

        Assert.False(result.Success);
        Assert.Equal(2, result.RemoveFeatureReport!.Count);
        // Consents without reviewed details authorize nothing: every removal is sent once, without force or unlock.
        var counts = WorkerCommandCounts.Of(s.Ctx.WorkerBridge);
        counts.AssertCount(WorkerHubMethods.RemoveGitWorktree, 4);
        Assert.All(counts.ArgsOf(WorkerHubMethods.RemoveGitWorktree), args =>
        {
            Assert.False(JsonSerializer.SerializeToElement(args).GetProperty("force").GetBoolean());
            Assert.False(JsonSerializer.SerializeToElement(args).GetProperty("unlock").GetBoolean());
        });
        Assert.All(counts.ArgsOf("DeleteBranch"), args => Assert.False(JsonSerializer.SerializeToElement(args).GetProperty("force").GetBoolean()));
        var unpreserved = s.Step(result.Outcome!, "source-01", RemoveFeatureSteps.Worktree);
        Assert.Equal(OperationReasonCodes.UnpreservedCommits, unpreserved.ReasonCode);
        Assert.Null(unpreserved.RequiredAuthorization);
        Assert.Equal(OperationReasonCodes.NestedRepository, s.Step(result.Outcome!, "source-03", RemoveFeatureSteps.Worktree).ReasonCode);
    }

    [Fact]
    public async Task A_discard_consent_reaches_only_the_repository_the_reviewed_details_showed_as_changed_and_is_bound_to_its_commit()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 2, withRoot: false);
        var plan = s.Plan(
            ("source-00", r => r with { HasUncommittedChanges = true, HeadCommit = "aaaa000000000000000000000000000000000000", IsLocked = true }),
            ("source-01", r => r));

        var result = await s.RemoveAsync(new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowUnlockWorktrees = true, AnalyzedPlan = plan });

        Assert.True(result.Success, result.Error);
        var removals = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgsOf(WorkerHubMethods.RemoveGitWorktree)
            .Select(a => JsonSerializer.SerializeToElement(a))
            .ToDictionary(a => a.GetProperty("worktreePath").GetString()!);
        var reviewedDirty = removals[s.WorktreePath("source-00")];
        Assert.True(reviewedDirty.GetProperty("force").GetBoolean());
        Assert.True(reviewedDirty.GetProperty("unlock").GetBoolean());
        Assert.Equal("aaaa000000000000000000000000000000000000", reviewedDirty.GetProperty("expectedHeadSha").GetString());
        var reviewedClean = removals[s.WorktreePath("source-01")];
        Assert.False(reviewedClean.GetProperty("force").GetBoolean());
        Assert.False(reviewedClean.GetProperty("unlock").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reviewedClean.GetProperty("expectedHeadSha").ValueKind);
    }

    [Fact]
    public async Task Work_saved_after_the_review_is_kept_and_the_message_says_it_changed_since_the_check()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 1, withRoot: false);
        // Reviewed as clean; dirty when the removal runs.
        s.Worktree["source-00"] = Scenario.Refused("UncommittedChanges", "fatal: contains modified or untracked files");

        var result = await s.RemoveAsync(new RemoveFeatureOptions { AllowDiscardUncommitted = true, AnalyzedPlan = s.Plan(("source-00", r => r)) });

        Assert.False(result.Success);
        Assert.Contains(WorkspaceFeatureOperations.ChangedSinceCheckMessage, result.Error, StringComparison.Ordinal);
        var removal = JsonSerializer.SerializeToElement(Assert.Single(WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgsOf(WorkerHubMethods.RemoveGitWorktree)));
        Assert.False(removal.GetProperty("force").GetBoolean());
    }

    [Fact]
    public async Task A_forced_branch_delete_is_leased_on_the_reviewed_tip_and_a_tip_that_moved_keeps_the_branch()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 1, withRoot: false);
        var plan = s.Plan(("source-00", r => r with { FeatureBranchAheadOfDefault = 2, FeatureBranchSha = "reviewed-tip", FeatureBranchExists = true }));
        s.LocalBranch["source-00"] = Scenario.BranchKept("TipChanged", "The branch has new commits since it was checked, so it was kept.", "moved-tip");

        var result = await s.RemoveAsync(new RemoveFeatureOptions { AllowForceDeleteLocalBranches = true, AnalyzedPlan = plan });

        Assert.True(result.Success, result.Error);
        var delete = JsonSerializer.SerializeToElement(Assert.Single(WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgsOf("DeleteBranch")));
        Assert.True(delete.GetProperty("force").GetBoolean());
        Assert.Equal("reviewed-tip", delete.GetProperty("expectedSha").GetString());
        var local = s.Step(result.Outcome!, "source-00", RemoveFeatureSteps.LocalBranch);
        Assert.Equal(OperationOutcomeStatus.Blocked, local.Status);
        Assert.Equal(OperationReasonCodes.ChangedSinceReview, local.ReasonCode);
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, Assert.Single(result.RemoveFeatureReport!).BranchOutcome);
    }

    [Fact]
    public async Task Without_reviewed_details_a_remote_branch_goes_only_with_a_local_branch_that_was_deleted_as_merged()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 2, withRoot: false);
        // source-00: merged, pushed. source-01: pushed, with commits git does not call merged.
        s.LocalBranch["source-00"] = Scenario.BranchDeleted("tip-00", hasUpstream: true);
        s.LocalBranch["source-01"] = Scenario.BranchKept("NotFullyMerged", "error: The branch is not fully merged.", "tip-01", hasUpstream: true);

        var result = await s.RemoveAsync(new RemoveFeatureOptions { DeleteLocalBranches = true, DeleteRemoteBranches = true });

        Assert.True(result.Success, result.Error);
        var remoteDeletes = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgsOf("DeleteBranch")
            .Select(a => JsonSerializer.SerializeToElement(a))
            .Where(a => a.GetProperty("isRemote").GetBoolean())
            .ToList();
        var remote = Assert.Single(remoteDeletes);
        Assert.Equal("source-00", remote.GetProperty("repositoryName").GetString());
        // The lease is the tip the local delete read, not a guess.
        Assert.Equal("tip-00", remote.GetProperty("expectedSha").GetString());
        Assert.False(remote.GetProperty("force").GetBoolean());

        var kept = s.Step(result.Outcome!, "source-01", RemoveFeatureSteps.RemoteBranch);
        Assert.Equal(OperationOutcomeStatus.Blocked, kept.Status);
        Assert.Equal(OperationReasonCodes.UnmergedCommits, kept.ReasonCode);
        var report = result.RemoveFeatureReport!.Single(r => r.RepositoryName == "source-01");
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, report.BranchOutcome);
        Assert.Equal(RemoveFeatureRemoteBranchOutcome.Kept, report.RemoteBranchOutcome);
    }

    [Fact]
    public async Task A_branch_git_calls_unmerged_is_deleted_when_a_merged_pull_request_merged_exactly_its_tip()
    {
        // Squash and rebase merges are not ancestors of the default branch. Without reviewed details the pull request is
        // looked up only for the repositories whose branch git kept, and the forced delete is leased on the merged commit.
        var gitHub = new PullRequestsHandler();
        gitHub.Pulls("source-00", """[{"number":5,"state":"closed","merged_at":"2026-11-20T10:00:00Z","html_url":"https://github.com/acme/source-00/pull/5","head":{"ref":"feat-safe","sha":"squashed-tip"},"base":{"ref":"main"}}]""");
        gitHub.Pulls("source-01", """[{"number":6,"state":"closed","merged_at":"2026-11-20T10:00:00Z","html_url":"https://github.com/acme/source-01/pull/6","head":{"ref":"feat-safe","sha":"merged-head"},"base":{"ref":"main"}}]""");
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: false, gitHub);
        var attempts = new ConcurrentDictionary<string, int>();
        Func<WorkerCommandResponse> KeptThenDeleted(string name, string tip) => () =>
            attempts.AddOrUpdate(name, 1, (_, n) => n + 1) == 1
                ? Scenario.BranchKept("NotFullyMerged", "error: The branch is not fully merged.", tip)()
                : Scenario.BranchDeleted(tip, hasUpstream: false)();
        s.LocalBranch["source-00"] = KeptThenDeleted("source-00", "squashed-tip");
        // The pull request of source-01 merged another commit than the local tip: commits were added after the merge.
        s.LocalBranch["source-01"] = KeptThenDeleted("source-01", "local-tip-with-more-commits");

        var result = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.True(result.Success, result.Error);
        var deletes = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgsOf("DeleteBranch").Select(a => JsonSerializer.SerializeToElement(a)).ToList();
        var forced = Assert.Single(deletes, d => d.GetProperty("force").GetBoolean());
        Assert.Equal("source-00", forced.GetProperty("repositoryName").GetString());
        Assert.Equal("squashed-tip", forced.GetProperty("expectedSha").GetString());
        Assert.Equal(RemoveFeatureBranchOutcome.Deleted, result.RemoveFeatureReport!.Single(r => r.RepositoryName == "source-00").BranchOutcome);
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, result.RemoveFeatureReport!.Single(r => r.RepositoryName == "source-01").BranchOutcome);
        // The clean repository never caused a pull request lookup.
        Assert.DoesNotContain(gitHub.Requests, r => r.Contains("source-02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_lease_keeps_the_remote_branch_and_the_Feature_is_still_finished()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 1, withRoot: false);
        s.LocalBranch["source-00"] = Scenario.BranchDeleted("tip-00", hasUpstream: true);
        s.RemoteBranch["source-00"] = Scenario.BranchKept("LeaseRefused", "Remote branch tip no longer matches this Feature.", null);

        var result = await s.RemoveAsync(new RemoveFeatureOptions { DeleteRemoteBranches = true });

        Assert.True(result.Success, result.Error);
        var report = Assert.Single(result.RemoveFeatureReport!);
        Assert.Equal(RemoveFeatureRemoteBranchOutcome.RefusedLease, report.RemoteBranchOutcome);
        Assert.Equal(OperationReasonCodes.LeaseRefused, s.Step(result.Outcome!, "source-00", RemoveFeatureSteps.RemoteBranch).ReasonCode);
        Assert.Empty(await s.ReadRowsAsync());
    }

    [Fact]
    public async Task A_remote_branch_delete_that_failed_keeps_the_Feature_and_the_retry_runs_only_that_step()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 2, withRoot: false);
        s.LocalBranch["source-00"] = Scenario.BranchDeleted("tip-00", hasUpstream: true);
        s.RemoteBranch["source-00"] = Scenario.BranchKept("Failed", "fatal: unable to access the remote: could not resolve host", null);

        var first = await s.RemoveAsync(new RemoveFeatureOptions { DeleteRemoteBranches = true });

        // Both folders are gone, and the removal is not reported as complete: a branch that was asked to go is still there.
        Assert.False(first.Success);
        Assert.Equal(2, first.RemoveFeatureReport!.Count);
        Assert.Contains("Remote branch was not deleted", first.Error, StringComparison.Ordinal);
        Assert.Equal(RemoveFeatureRemoteBranchOutcome.Failed, first.RemoveFeatureReport!.Single(r => r.RepositoryName == "source-00").RemoteBranchOutcome);
        var rows = await s.ReadRowsAsync();
        Assert.All(rows, r => Assert.Equal(WorkspaceFeatureRepositoryState.Removed, r.State));
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, await s.ReadFeatureStateAsync());
        Assert.Contains("Remote branch was not deleted", rows.Single(r => r.WorktreePath == s.WorktreePath("source-00")).LastError, StringComparison.Ordinal);

        // The network is back. The retry does not touch a worktree and does not repeat the local delete.
        var before = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).Total;
        s.RemoteBranch.Remove("source-00", out _);
        var second = await s.RemoveAsync(new RemoveFeatureOptions { DeleteRemoteBranches = true });

        Assert.True(second.Success, second.Error);
        var retry = WorkerCommandCounts.Since(s.Ctx.WorkerBridge, before);
        retry.AssertNone(WorkerHubMethods.RemoveGitWorktree, WorkerHubMethods.InspectWorktree);
        var delete = JsonSerializer.SerializeToElement(Assert.Single(retry.ArgsOf("DeleteBranch")));
        Assert.True(delete.GetProperty("isRemote").GetBoolean());
        Assert.Equal("source-00", delete.GetProperty("repositoryName").GetString());
        // The identity of the step survived: the lease is the tip recorded the first time, although the local branch is gone.
        Assert.Equal("tip-00", delete.GetProperty("expectedSha").GetString());
        Assert.Empty(await s.ReadRowsAsync());
    }

    [Fact]
    public async Task A_branch_that_was_kept_on_purpose_is_recorded_as_kept_not_as_an_error()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 1, withRoot: false);

        var result = await s.RemoveAsync(new RemoveFeatureOptions { DeleteLocalBranches = false });

        Assert.True(result.Success, result.Error);
        WorkerCommandCounts.Of(s.Ctx.WorkerBridge).AssertNone("DeleteBranch");
        var local = s.Step(result.Outcome!, "source-00", RemoveFeatureSteps.LocalBranch);
        Assert.Equal(OperationOutcomeStatus.Skipped, local.Status);
        Assert.Equal(OperationReasonCodes.NotRequested, local.ReasonCode);
        Assert.Equal(RemoveFeatureBranchOutcome.Kept, Assert.Single(result.RemoveFeatureReport!).BranchOutcome);
        Assert.False(result.Outcome!.NeedsAttention);
    }

    [Fact]
    public async Task A_lost_reply_is_settled_by_reading_the_worktree_list_and_the_removal_is_never_sent_twice()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: false);
        // git removed source-00 and the answer was lost; source-01 timed out before git ran; for source-02 the list cannot be read either.
        s.Worktree["source-00"] = Scenario.NoAnswer("Worker command timed out: no response or output for 120s.");
        s.Worktree["source-01"] = Scenario.NoAnswer("Worker command timed out: no response or output for 120s.");
        s.Worktree["source-02"] = Scenario.NoAnswer("The connection to the Worker was lost.");
        s.Registered["source-00"] = false;
        s.Registered["source-01"] = true;
        s.Registered["source-02"] = null;

        var result = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.False(result.Success);
        var counts = WorkerCommandCounts.Of(s.Ctx.WorkerBridge);
        counts.AssertCount(WorkerHubMethods.RemoveGitWorktree, 3);
        counts.AssertCount(WorkerHubMethods.ListGitWorktrees, 3);

        // Removed after all: recorded as removed, and its branch step ran.
        Assert.Equal(OperationOutcomeStatus.Succeeded, s.Step(result.Outcome!, "source-00", RemoveFeatureSteps.Worktree).Status);
        Assert.Equal("source-00", Assert.Single(result.RemoveFeatureReport!).RepositoryName);
        // Verified as still there: a failure, with the cause.
        var notRemoved = s.Step(result.Outcome!, "source-01", RemoveFeatureSteps.Worktree);
        Assert.Equal(OperationOutcomeStatus.Failed, notRemoved.Status);
        Assert.Contains("timed out", notRemoved.Message, StringComparison.Ordinal);
        // Not known: neither done nor failed, and the whole run says so.
        var unknown = s.Step(result.Outcome!, "source-02", RemoveFeatureSteps.Worktree);
        Assert.Equal(OperationOutcomeStatus.Unknown, unknown.Status);
        Assert.Equal(OperationReasonCodes.OutcomeUnknown, unknown.ReasonCode);
        Assert.True(unknown.Invalidated.HasFlag(OperationFactGroups.WorktreeRegistration));
        Assert.Equal(OperationOutcomeStatus.Unknown, result.Outcome!.Status);

        var rows = await s.ReadRowsAsync();
        Assert.Equal(WorkspaceFeatureRepositoryState.Removed, rows.Single(r => r.WorktreePath == s.WorktreePath("source-00")).State);
        Assert.Equal(WorkspaceFeatureRepositoryState.Removing, rows.Single(r => r.WorktreePath == s.WorktreePath("source-02")).State);
    }

    [Fact]
    public async Task A_removal_cut_short_keeps_what_it_removed_and_the_next_one_continues_from_there()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: false);
        using var shutdown = new CancellationTokenSource();
        // The App shuts down while source-01 is being removed.
        s.Worktree["source-01"] = () =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        };
        s.MaxParallel = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RemoveAsync(new RemoveFeatureOptions(), shutdown.Token));

        var rows = await s.ReadRowsAsync();
        Assert.Equal(WorkspaceFeatureRepositoryState.Removed, rows.Single(r => r.WorktreePath == s.WorktreePath("source-00")).State);
        Assert.Equal(WorkspaceFeatureLifecycleState.Removing, await s.ReadFeatureStateAsync());

        // After the restart nothing is removing: the Feature is released for another Remove.
        using var queue = new FeatureRemovalQueue(
            s.Ctx.Resolve<IServiceScopeFactory>(), new NoFolderCleanup(), s.Ctx.Resolve<IWorkspaceRemoteRefresh>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRemovalQueue>.Instance);
        Assert.Equal(1, await queue.RecoverInterruptedRemovalsAsync(CancellationToken.None));
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, await s.ReadFeatureStateAsync());

        s.Worktree.Remove("source-01", out _);
        var before = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).Total;
        var second = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.True(second.Success, second.Error);
        var retry = WorkerCommandCounts.Since(s.Ctx.WorkerBridge, before);
        Assert.DoesNotContain(s.WorktreePath("source-00"), retry.ArgValues(WorkerHubMethods.RemoveGitWorktree, "worktreePath"));
        Assert.Equal(3, second.RemoveFeatureReport!.Count);
    }

    [Fact]
    public async Task A_tag_pinned_repository_and_a_read_only_snapshot_have_no_branch_step_to_run()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: false);
        await s.MutateRowAsync("source-00", r => r.PinnedTag = "v1.2.0");
        await s.MutateRowAsync("source-01", r => r.IsReferenceSnapshot = true);

        var result = await s.RemoveAsync(new RemoveFeatureOptions { DeleteLocalBranches = true, DeleteRemoteBranches = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);
        Assert.Equal(["source-02"], WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgValues("DeleteBranch", "repositoryName").Distinct());
        foreach (var name in new[] { "source-00", "source-01" })
        {
            Assert.Equal(OperationReasonCodes.NotApplicable, s.Step(result.Outcome!, name, RemoveFeatureSteps.LocalBranch).ReasonCode);
            Assert.Equal(OperationOutcomeStatus.Skipped, s.Step(result.Outcome!, name, RemoveFeatureSteps.RemoteBranch).Status);
            Assert.Equal(RemoveFeatureBranchOutcome.NotApplicable, result.RemoveFeatureReport!.Single(r => r.RepositoryName == name).BranchOutcome);
        }
    }

    [Fact]
    public async Task The_root_is_removed_last_and_only_after_every_source_and_a_root_kept_for_its_own_reason_says_so()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: true);
        s.Worktree[Root] = Scenario.Refused("UncommittedChanges", "fatal: contains modified or untracked files");

        var result = await s.RemoveAsync(new RemoveFeatureOptions());

        Assert.False(result.Success);
        var removals = WorkerCommandCounts.Of(s.Ctx.WorkerBridge).ArgValues(WorkerHubMethods.RemoveGitWorktree, "worktreePath");
        Assert.Equal(s.WorktreePath(Root), removals[^1]);
        Assert.Equal(4, removals.Count);
        // Dirty, not waiting: the message is the root's own, without the "kept until" line.
        Assert.Equal(OperationReasonCodes.UncommittedChanges, s.Step(result.Outcome!, Root, RemoveFeatureSteps.Worktree).ReasonCode);
        Assert.DoesNotContain(WorkspaceFeatureOperations.RootKeptMessage, result.Error, StringComparison.Ordinal);
        WorkerCommandCounts.Of(s.Ctx.WorkerBridge).AssertNone(WorkerHubMethods.CleanupFeatureFolder);
    }

    [Fact]
    public async Task The_typed_outcome_of_a_partial_removal_reads_the_same_as_JSON()
    {
        await using var s = await Scenario.CreateAsync(sourceCount: 3, withRoot: false);
        s.Worktree["source-01"] = Scenario.Refused("OperationInProgress", "A merge, rebase or cherry-pick is not finished in this folder.");

        var result = await s.RemoveAsync(new RemoveFeatureOptions());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var outcome = json.RootElement.GetProperty("outcome");
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("RemoveFeature", outcome.GetProperty("operation").GetString());
        Assert.Equal("Partial", outcome.GetProperty("status").GetString());
        Assert.Equal(s.FeatureContextId.Value, outcome.GetProperty("contextId").GetInt32());
        Assert.Equal(FeatureName, outcome.GetProperty("contextName").GetString());
        var blocked = outcome.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("status").GetString() == "Blocked");
        Assert.Equal("source-01", blocked.GetProperty("target").GetProperty("name").GetString());
        Assert.Equal("OperationInProgress", blocked.GetProperty("steps")[0].GetProperty("reasonCode").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("removeFeatureReport").GetArrayLength());
    }

    [Fact]
    public async Task A_late_hook_from_a_removed_worktree_is_not_attributed_to_the_Feature()
    {
        // The removal is incomplete, so the rows are still there. An event that names the path of a worktree that is
        // gone must not write state for the Feature again.
        await using var s = await Scenario.CreateAsync(sourceCount: 2, withRoot: false);
        s.Worktree["source-01"] = Scenario.Refused("UncommittedChanges", "fatal: contains modified or untracked files");
        Assert.False((await s.RemoveAsync(new RemoveFeatureOptions())).Success);

        await using var scope = s.Ctx.CreateScope();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();
        var removed = await attributor.ResolveAsync(s.Ctx.WorkspaceId, s.RepositoryId("source-00"), s.WorktreePath("source-00"), null);
        var kept = await attributor.ResolveAsync(s.Ctx.WorkspaceId, s.RepositoryId("source-01"), s.WorktreePath("source-01"), null);

        Assert.Null(removed);
        Assert.Equal(s.FeatureContextId, kept);
    }

    /// <summary>Answers GitHub's pull request list per repository; everything else is 404.</summary>
    private sealed class PullRequestsHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodyByPath = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void Pulls(string repositoryName, string json) => _bodyByPath[$"/repos/acme/{repositoryName}/pulls"] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(_bodyByPath.TryGetValue(request.RequestUri.AbsolutePath, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoFolderCleanup : IFeatureFolderCleanupService
    {
        public void RequestSweep(int workspaceId)
        {
        }

        public Task SweepAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ArmRetry(int workspaceId)
        {
        }
    }

    /// <summary>A Feature with N source repositories (and optionally the Workspace repository as root) and a Worker that answers per repository.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly Dictionary<string, int> _repositoryIdByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _linkIdByName = new(StringComparer.Ordinal);

        public required SyncStateTestContext Ctx { get; init; }
        public WorkspaceFeatureContextId FeatureContextId { get; private set; }
        public int MaxParallel { get; set; } = 4;

        /// <summary>Repository name -> the answer to RemoveGitWorktree. Not listed: removed.</summary>
        public ConcurrentDictionary<string, Func<WorkerCommandResponse>> Worktree { get; } = new();

        /// <summary>Repository name -> the answer to a local DeleteBranch. Not listed: deleted, never pushed.</summary>
        public ConcurrentDictionary<string, Func<WorkerCommandResponse>> LocalBranch { get; } = new();

        /// <summary>Repository name -> the answer to a remote DeleteBranch. Not listed: deleted.</summary>
        public ConcurrentDictionary<string, Func<WorkerCommandResponse>> RemoteBranch { get; } = new();

        /// <summary>Repository name -> whether ListGitWorktrees still lists its worktree; null fails the listing.</summary>
        public ConcurrentDictionary<string, bool?> Registered { get; } = new();

        public static Func<WorkerCommandResponse> Refused(string failureKind, string message)
            => () => new WorkerCommandResponse(false, new { success = false, errorCode = failureKind, errorMessage = message, failureKind }, message);

        public static Func<WorkerCommandResponse> NoAnswer(string error)
            => () => new WorkerCommandResponse(false, null, error);

        public static Func<WorkerCommandResponse> BranchDeleted(string branchSha, bool hasUpstream)
            => () => new WorkerCommandResponse(true, new { success = true, branchSha, hasUpstream }, null);

        public static Func<WorkerCommandResponse> BranchKept(string failureKind, string message, string? branchSha, bool hasUpstream = false)
            => () => new WorkerCommandResponse(false, new { success = false, errorMessage = message, failureKind, branchSha, hasUpstream }, message);

        public static async Task<Scenario> CreateAsync(int sourceCount, bool withRoot, HttpMessageHandler? gitHub = null)
        {
            Scenario? scenario = null;
            var ctx = await SyncStateTestContext.CreateAsync(
                userToken: gitHub is null ? null : "test-token",
                configureServices: services =>
                {
                    services.PostConfigure<GrayMoon.App.Models.WorkspaceOptions>(o => o.MaxParallelOperations = scenario?.MaxParallel ?? 4);
                    if (gitHub is not null)
                        services.AddSingleton(new HttpClient(gitHub));
                });
            scenario = new Scenario { Ctx = ctx };
            await scenario.SeedAsync(sourceCount, withRoot);
            scenario.WireWorker();
            return scenario;
        }

        public string WorktreePath(string repositoryName)
            => repositoryName == Root
                ? $@"C:\gm-test-root\.graymoon\test-ws\features\{FeatureName}"
                : $@"C:\gm-test-root\.graymoon\test-ws\features\{FeatureName}\{repositoryName}";

        public int RepositoryId(string repositoryName) => _repositoryIdByName[repositoryName];

        public async Task<OperationResult> RemoveAsync(RemoveFeatureOptions options, CancellationToken cancellationToken = default)
        {
            await using var scope = Ctx.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
                .RemoveFeatureAsync(FeatureContextId, options, cancellationToken: cancellationToken);
        }

        public OperationStepOutcome Step(OperationOutcome outcome, string repositoryName, string step)
            => outcome.ForWorkspaceRepository(_linkIdByName[repositoryName])!.Step(step)
               ?? throw new InvalidOperationException($"{repositoryName} has no {step} step.");

        /// <summary>Reviewed details: clean, on the Feature branch, never pushed, unless <paramref name="rows"/> say otherwise.</summary>
        public RemoveFeaturePlan Plan(params (string Name, Func<RemoveFeatureRepositoryPlanSeed, RemoveFeatureRepositoryPlanSeed> Change)[] rows) => new()
        {
            Success = true,
            Repositories = rows
                .Select(row => row.Change(new RemoveFeatureRepositoryPlanSeed()).ToPlan(_linkIdByName[row.Name], row.Name, WorktreePath(row.Name)))
                .ToList(),
        };

        public async Task<List<WorkspaceFeatureRepository>> ReadRowsAsync()
        {
            await using var scope = Ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.WorkspaceFeatureRepositories.AsNoTracking()
                .Where(r => r.WorkspaceFeatureContextId == FeatureContextId.Value)
                .ToListAsync();
        }

        public async Task<WorkspaceFeatureLifecycleState> ReadFeatureStateAsync()
        {
            await using var scope = Ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.WorkspaceFeatures.AsNoTracking().Where(f => f.Name == FeatureName).Select(f => f.LifecycleState).SingleAsync();
        }

        public async Task MutateRowAsync(string repositoryName, Action<WorkspaceFeatureRepository> mutate)
        {
            await using var scope = Ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linkId = _linkIdByName[repositoryName];
            var row = await db.WorkspaceFeatureRepositories.SingleAsync(
                r => r.WorkspaceFeatureContextId == FeatureContextId.Value && r.WorkspaceRepositoryId == linkId);
            mutate(row);
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Ctx.DisposeAsync();

        private async Task SeedAsync(int sourceCount, bool withRoot)
        {
            await using var scope = Ctx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The fixture's own repository is not part of this Feature: every name is one this scenario made.
            db.WorkspaceRepositories.RemoveRange(await db.WorkspaceRepositories.ToListAsync());
            await db.SaveChangesAsync();
            var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();

            var names = Enumerable.Range(0, sourceCount).Select(i => $"source-{i:00}").ToList();
            if (withRoot)
                names.Insert(0, Root);

            var feature = new WorkspaceFeature
            {
                WorkspaceId = Ctx.WorkspaceId,
                Name = FeatureName,
                LifecycleState = WorkspaceFeatureLifecycleState.Ready,
                BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.WorkspaceFeatures.Add(feature);
            await db.SaveChangesAsync();
            var context = new WorkspaceFeatureContext
            {
                WorkspaceId = Ctx.WorkspaceId,
                Kind = WorkspaceFeatureContextKind.Feature,
                WorkspaceFeatureId = feature.WorkspaceFeatureId,
                CreatedAt = DateTime.UtcNow,
                IsInSync = true,
            };
            db.WorkspaceFeatureContexts.Add(context);
            await db.SaveChangesAsync();
            FeatureContextId = new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);

            foreach (var name in names)
            {
                var repository = new Repository
                {
                    ConnectorId = connectorId,
                    RepositoryName = name,
                    OrgName = "acme",
                    Visibility = "Public",
                    CloneUrl = $"https://github.com/acme/{name}.git",
                };
                db.Repositories.Add(repository);
                await db.SaveChangesAsync();
                var link = new WorkspaceRepositoryLink
                {
                    WorkspaceId = Ctx.WorkspaceId,
                    RepositoryId = repository.RepositoryId,
                    Role = name == Root ? WorkspaceRepositoryRole.Workspace : WorkspaceRepositoryRole.Source,
                    BranchName = "main",
                    DefaultBranchName = "main",
                    SyncStatus = RepoSyncStatus.InSync,
                };
                db.WorkspaceRepositories.Add(link);
                await db.SaveChangesAsync();
                db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
                {
                    WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    WorktreePath = WorktreePath(name),
                    State = WorkspaceFeatureRepositoryState.Ready,
                    BaseCommitSha = "abc123",
                    CreatedAt = DateTime.UtcNow,
                });
                _repositoryIdByName[name] = repository.RepositoryId;
                _linkIdByName[name] = link.WorkspaceRepositoryId;
            }

            await db.SaveChangesAsync();
        }

        private void WireWorker()
        {
            string NameOfPath(string? worktreePath)
                => _linkIdByName.Keys.Single(n => string.Equals(WorktreePath(n), worktreePath, StringComparison.OrdinalIgnoreCase));

            Ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, args =>
            {
                var name = NameOfPath(JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString());
                return Worktree.TryGetValue(name, out var answer)
                    ? answer()
                    : new WorkerCommandResponse(true, new { success = true, branch = name == Root ? FeatureName : FeatureName, headSha = "abc123" }, null);
            });
            Ctx.WorkerBridge.Respond("DeleteBranch", args =>
            {
                var json = JsonSerializer.SerializeToElement(args);
                var name = json.GetProperty("repositoryName").GetString()!;
                var answers = json.GetProperty("isRemote").GetBoolean() ? RemoteBranch : LocalBranch;
                return answers.TryGetValue(name, out var answer)
                    ? answer()
                    : new WorkerCommandResponse(true, new { success = true, branchSha = "tip-" + name, hasUpstream = false }, null);
            });
            Ctx.WorkerBridge.Respond(WorkerHubMethods.ListGitWorktrees, args =>
            {
                // The main repository path ends with the repository's folder name.
                var mainPath = JsonSerializer.SerializeToElement(args).GetProperty("mainRepositoryPath").GetString() ?? "";
                var name = _linkIdByName.Keys.FirstOrDefault(n => mainPath.EndsWith(n, StringComparison.OrdinalIgnoreCase)) ?? Root;
                if (!Registered.TryGetValue(name, out var registered) || registered is null)
                    return new WorkerCommandResponse(false, null, "git worktree list failed");
                var worktrees = registered == true
                    ? new object[] { new { worktreePath = mainPath }, new { worktreePath = WorktreePath(name) } }
                    : [new { worktreePath = mainPath }];
                return new WorkerCommandResponse(true, new { worktrees }, null);
            });
            Ctx.WorkerBridge.Respond(WorkerHubMethods.CleanupFeatureFolder, new { outcome = "Removed", remainingFileCount = 0 });
        }
    }

    /// <summary>The fields of a reviewed repository a test changes; everything else is "clean, on the Feature branch, never pushed".</summary>
    private sealed record RemoveFeatureRepositoryPlanSeed
    {
        public bool HasUncommittedChanges { get; init; }
        public bool IsLocked { get; init; }
        public string? HeadCommit { get; init; } = "abc123";
        public int? FeatureBranchAheadOfDefault { get; init; } = 0;
        public string? FeatureBranchSha { get; init; } = "abc123";
        public bool? FeatureBranchExists { get; init; } = true;
        public bool? FeatureBranchHasUpstream { get; init; } = false;

        public RemoveFeatureRepositoryPlan ToPlan(int workspaceRepositoryId, string name, string worktreePath) => new()
        {
            WorkspaceRepositoryId = workspaceRepositoryId,
            RepositoryName = name,
            WorktreePath = worktreePath,
            WorktreeExists = true,
            LiveStatusEstablished = true,
            BranchName = FeatureName,
            FeatureBranchName = FeatureName,
            CheckedOutBranch = FeatureName,
            HeadCommit = HeadCommit,
            HasUncommittedChanges = HasUncommittedChanges,
            IsLocked = IsLocked,
            FeatureBranchAheadOfDefault = FeatureBranchAheadOfDefault,
            FeatureBranchSha = FeatureBranchSha,
            FeatureBranchExists = FeatureBranchExists,
            FeatureBranchHasUpstream = FeatureBranchHasUpstream,
            AheadOfDefault = FeatureBranchAheadOfDefault,
        };
    }
}
