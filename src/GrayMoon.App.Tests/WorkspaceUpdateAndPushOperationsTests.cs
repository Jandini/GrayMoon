using System.Reflection;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The combined Update and Push entry point: one decision policy for every caller. The facades below it are replaced by
/// recording fakes so the tests assert which calls the coordinator makes (and, as important, which it never makes).
/// </summary>
public sealed class WorkspaceUpdateAndPushOperationsTests
{
    private const int WorkspaceId = 5;
    private static readonly WorkspaceFeatureContextId Context = new(11);

    private static StartUpdateAndPush Start(int? maxLevel = null, bool restore = false, string? message = "msg", bool includeDeps = false)
        => new(WorkspaceId, Context, maxLevel, message, includeDeps, restore, "test");

    [Fact]
    public async Task Pipelined_success_completes_and_never_runs_the_sequential_flow()
    {
        var h = new Harness();
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(Ids(1)), OperationResult.Ok(), PushedRepoCount: 2);

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.Completed, result.Status);
        Assert.True(result.Success);
        Assert.True(result.Pipelined);
        Assert.Equal(2, result.PushedRepoCount);
        Assert.Equal(["UpdateAndPush"], h.Calls);
    }

    [Fact]
    public async Task Pipelined_with_nothing_pushed_reports_nothing_to_push()
    {
        var h = new Harness();
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(), OperationResult.Ok(), PushedRepoCount: 0);

        var result = await h.Run(Start(maxLevel: 2));

        Assert.Equal(UpdateAndPushStatus.NothingToPush, result.Status);
        Assert.True(result.Success);
        Assert.Equal("Up to Level 2 updated. Nothing to push.", result.Summary);
    }

    [Fact]
    public async Task Pipelined_push_failure_without_an_exception_is_a_failed_run_and_keeps_level_details()
    {
        var h = new Harness();
        var failure = OperationResult.Fail("Stopped", null, new Dictionary<int, string> { [3] = "Timed out waiting for Acme.Api 1.2.3" });
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(Ids(1, 2)), failure, PushedRepoCount: 1);

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.PushFailed, result.Status);
        Assert.False(result.Success);
        Assert.Same(failure, result.Push);
        Assert.Equal(1, result.PushedRepoCount);
        Assert.Contains("level 3", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Acme.Api 1.2.3", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipelined_update_failure_is_a_failed_run_and_keeps_the_levels_already_pushed()
    {
        var h = new Harness();
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Failed(), OperationResult.Ok(), PushedRepoCount: 1);

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.UpdateFailed, result.Status);
        Assert.False(result.Success);
        Assert.Equal(1, result.PushedRepoCount);
    }

    [Fact]
    public async Task Failed_sequential_update_returned_without_an_exception_never_plans_or_pushes()
    {
        var h = new Harness();
        h.Update.Update = DependencyUpdateRunResult.Failed();

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.UpdateFailed, result.Status);
        Assert.Equal(["UpdateAndPush", "Update"], h.Calls);
        Assert.Empty(h.Push.PushCalls);
    }

    [Fact]
    public async Task Fallback_updates_live_with_the_same_context_level_and_options()
    {
        var h = new Harness();
        h.Update.Update = DependencyUpdateRunResult.Failed();

        await h.Run(Start(maxLevel: 3, message: "Bump", includeDeps: true, restore: true));

        var pipeline = Assert.Single(h.Update.PipelineCalls);
        var update = Assert.Single(h.Update.UpdateCalls);
        Assert.Equal(Context, pipeline.Context);
        Assert.Equal(Context, update.Context);
        Assert.Equal(3, update.MaxLevel);
        Assert.Null(update.RepoIdsToUpdate);
        Assert.Equal("Bump", update.CommitMessage);
        Assert.True(update.IncludeDeps);
        Assert.Equal((3, "Bump", true, true), (pipeline.MaxLevel, pipeline.CommitMessage, pipeline.IncludeDeps, pipeline.Restore));
        Assert.Equal(pipeline.RunId, update.RunId);
    }

    [Fact]
    public async Task Empty_fresh_plan_after_a_successful_update_is_nothing_to_push()
    {
        var h = new Harness();
        h.Push.Plan = new WorkspacePushPlan(Ids(), new HashSet<string>(), false);

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.NothingToPush, result.Status);
        Assert.Empty(h.Push.PushCalls);
    }

    [Fact]
    public async Task Sequential_push_uses_the_fresh_plan_synchronized_and_the_explicit_restore_choice()
    {
        var h = new Harness();
        h.Update.Update = DependencyUpdateRunResult.Ok(Ids(4));
        h.Push.Plan = new WorkspacePushPlan(Ids(4, 5), new HashSet<string> { "Acme.Api" }, true);

        var result = await h.Run(Start(maxLevel: 2, restore: false));

        var push = Assert.Single(h.Push.PushCalls);
        Assert.True(push.Synchronized);
        Assert.False(push.Restore);
        Assert.Equal(Context, push.Context);
        Assert.Equal(Ids(4, 5), push.RepositoryIds.ToHashSet());
        Assert.Equal(Ids(4), push.SyncedRepoIds!.ToHashSet());
        Assert.Equal(2, h.Push.PlanMaxLevels.Single());
        Assert.Equal(UpdateAndPushStatus.Completed, result.Status);
        Assert.Equal(2, result.PushedRepoCount);
    }

    [Fact]
    public async Task Returned_push_failure_in_the_sequential_flow_is_push_failed()
    {
        var h = new Harness();
        h.Push.PushResult = OperationResult.Fail("Stopped", null, new Dictionary<int, string> { [2] = "boom" });

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.PushFailed, result.Status);
        Assert.Contains("level 2", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synchronized_push_refusal_asks_for_approval_and_never_pushes_normally()
    {
        var h = new Harness();
        h.Push.ThrowNotPossibleWhenSynchronized = true;

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.ApprovalRequired, result.Status);
        Assert.True(result.NeedsApproval);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.PendingApprovalId));
        Assert.Contains("2 required package mappings", result.Summary, StringComparison.Ordinal);
        Assert.All(h.Push.PushCalls, call => Assert.True(call.Synchronized));
    }

    [Fact]
    public async Task Approval_pushes_normally_once_without_running_the_update_again()
    {
        var h = new Harness();
        h.Push.ThrowNotPossibleWhenSynchronized = true;
        h.Update.Update = DependencyUpdateRunResult.Ok(Ids(4));
        var pending = await h.Run(Start(restore: true));
        h.Calls.Clear();

        var result = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, pending.PendingApprovalId!));

        Assert.Equal(UpdateAndPushStatus.Completed, result.Status);
        Assert.Equal(pending.RunId, result.RunId);
        Assert.DoesNotContain("Update", h.Calls);
        Assert.DoesNotContain("UpdateAndPush", h.Calls);
        var normal = h.Push.PushCalls.Last();
        Assert.False(normal.Synchronized);
        Assert.True(normal.Restore);
        Assert.Contains("RestoreSynced", h.Calls);
    }

    [Fact]
    public async Task Approval_is_single_use_and_bound_to_its_context()
    {
        var h = new Harness();
        h.Push.ThrowNotPossibleWhenSynchronized = true;
        var pending = await h.Run(Start());
        var pushesBefore = h.Push.PushCalls.Count;

        var wrongContext = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, new WorkspaceFeatureContextId(99), pending.PendingApprovalId!));
        var unknown = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, "nope"));
        Assert.Equal(UpdateAndPushStatus.Rejected, wrongContext.Status);
        Assert.Equal(UpdateAndPushStatus.Rejected, unknown.Status);
        Assert.Equal(pushesBefore, h.Push.PushCalls.Count);

        var first = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, pending.PendingApprovalId!));
        var replay = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, pending.PendingApprovalId!));
        Assert.Equal(UpdateAndPushStatus.Completed, first.Status);
        Assert.Equal(UpdateAndPushStatus.Rejected, replay.Status);
        Assert.Equal(pushesBefore + 1, h.Push.PushCalls.Count);
    }

    [Fact]
    public async Task Approval_pushes_only_repositories_that_are_still_pending_and_were_approved()
    {
        var h = new Harness();
        h.Push.ThrowNotPossibleWhenSynchronized = true;
        h.Push.Plan = new WorkspacePushPlan(Ids(1, 2), new HashSet<string>(), true);
        var pending = await h.Run(Start());
        h.Push.Plan = new WorkspacePushPlan(Ids(2, 3), new HashSet<string>(), true);

        await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, pending.PendingApprovalId!));

        Assert.Equal(Ids(2), h.Push.PushCalls.Last().RepositoryIds.ToHashSet());
    }

    [Fact]
    public async Task Approval_with_nothing_left_to_push_does_not_push()
    {
        var h = new Harness();
        h.Push.ThrowNotPossibleWhenSynchronized = true;
        var pending = await h.Run(Start());
        var pushesBefore = h.Push.PushCalls.Count;
        h.Push.Plan = new WorkspacePushPlan(Ids(), new HashSet<string>(), false);

        var result = await h.Run(new ApproveNormalPushContinuation(WorkspaceId, Context, pending.PendingApprovalId!));

        Assert.Equal(UpdateAndPushStatus.NothingToPush, result.Status);
        Assert.Equal(pushesBefore, h.Push.PushCalls.Count);
    }

    [Fact]
    public async Task Unavailable_context_is_refused_before_anything_runs()
    {
        var h = new Harness { ContextMissing = true };

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.Rejected, result.Status);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Feature_that_needs_repair_is_refused_without_falling_back_to_the_workspace()
    {
        var h = new Harness { LifecycleState = "NeedsRepair", IsSpecialWorkspace = false };

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.Rejected, result.Status);
        Assert.Equal(Context, result.ContextId);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Exceptions_are_recorded_in_the_outcome_then_rethrown()
    {
        var h = new Harness();
        h.Update.ThrowOnPipeline = new InvalidOperationException("worker is gone");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(Start()));

        Assert.Equal("worker is gone", ex.Message);
        var outcome = h.Outcomes.Get(WorkspaceId, Context);
        Assert.NotNull(outcome);
        Assert.Contains("worker is gone", outcome!.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_recorded_as_a_failure()
    {
        var h = new Harness();
        h.Update.ThrowOnPipeline = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(Start()));

        Assert.Null(h.Outcomes.Get(WorkspaceId, Context));
    }

    [Fact]
    public async Task Default_branch_targets_cover_every_context_repository_up_to_the_level_and_skip_tags()
    {
        var h = new Harness();
        h.Update.UpdatePlan = [new SyncDependenciesRepoPayload(1, "a", 1, []), new SyncDependenciesRepoPayload(2, "b", 2, []), new SyncDependenciesRepoPayload(3, "c", 1, []), new SyncDependenciesRepoPayload(4, "d", 1, [])];
        h.Links =
        [
            Link(1, "a", level: 1, branch: "main", defaultBranch: "main"),
            Link(2, "b", level: 2, branch: "main", defaultBranch: "main"),
            Link(3, "c", level: 1, branch: "main", defaultBranch: "main", tag: "v1.0.0"),
            Link(4, "d", level: 1, branch: "feature/x", defaultBranch: "main"),
            Link(5, "e", level: 1, branch: "main", defaultBranch: "main"),
        ];
        // 250 more rows prove the list is not capped like the notification popup's 100.
        h.Links = h.Links.Concat(Enumerable.Range(100, 250).Select(i => Link(i, $"r{i}", 1, "main", "main"))).ToList();
        h.Update.UpdatePlan = h.Update.UpdatePlan.Concat(Enumerable.Range(100, 250).Select(i => new SyncDependenciesRepoPayload(i, $"r{i}", 1, []))).ToList();

        var all = await h.Operations.GetDefaultBranchTargetsAsync(WorkspaceId, Context, null, CancellationToken.None);
        var level1 = await h.Operations.GetDefaultBranchTargetsAsync(WorkspaceId, Context, 1, CancellationToken.None);

        Assert.Equal(252, all.Count);
        Assert.DoesNotContain(all, t => t.RepositoryId is 3 or 4 or 5);
        Assert.Equal(251, level1.Count);
        Assert.DoesNotContain(level1, t => t.RepositoryId == 2);
    }

    [Fact]
    public async Task Run_is_refused_when_a_repository_moved_onto_its_default_branch_after_the_warning()
    {
        var h = new Harness();
        h.Links = [Link(1, "a", 1, "main", "main"), Link(2, "b", 1, "main", "main")];

        var result = await h.Run(Start() with { ReviewedDefaultBranchRepoIds = Ids(1) });

        Assert.Equal(UpdateAndPushStatus.Rejected, result.Status);
        Assert.Contains("b", result.Summary);
        Assert.DoesNotContain("a,", result.Summary);
        Assert.Empty(h.Update.PipelineCalls);
        Assert.Empty(h.Update.UpdateCalls);
        Assert.Empty(h.Push.PushCalls);
    }

    [Fact]
    public async Task Run_proceeds_when_the_default_branch_repositories_are_the_reviewed_ones_or_out_of_scope()
    {
        var h = new Harness();
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(Ids(1)), OperationResult.Ok(), PushedRepoCount: 1);
        h.Links =
        [
            Link(1, "a", 1, "main", "main"),
            Link(2, "b", 2, "main", "main"),
            Link(3, "c", 1, "main", "main", tag: "v1.0.0"),
        ];

        var result = await h.Run(Start(maxLevel: 1) with { ReviewedDefaultBranchRepoIds = Ids(1) });

        Assert.Equal(UpdateAndPushStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Run_without_a_reviewed_set_does_not_read_the_branch_state()
    {
        var h = new Harness();
        h.Update.Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(Ids(1)), OperationResult.Ok(), PushedRepoCount: 1);
        h.Links = [Link(1, "a", 1, "main", "main")];

        var result = await h.Run(Start());

        Assert.Equal(UpdateAndPushStatus.Completed, result.Status);
        Assert.Equal(0, h.LinkReads);
    }

    [Fact]
    public async Task Review_without_default_branch_repositories_skips_the_update_plan()
    {
        var h = new Harness();
        h.Links = [Link(1, "a", 1, "feature/x", "main")];

        var review = await h.Operations.GetDefaultBranchReviewAsync(WorkspaceId, Context, null, CancellationToken.None);

        Assert.Empty(review.Targets);
        Assert.Empty(review.OnDefaultBranchRepoIds);
        Assert.Equal(0, h.Update.PlanReads);
    }

    [Fact]
    public async Task Review_lists_warned_repositories_and_the_whole_default_branch_baseline()
    {
        var h = new Harness();
        h.Update.UpdatePlan = [new SyncDependenciesRepoPayload(1, "a", 1, [])];
        h.Links = [Link(1, "a", 1, "main", "main"), Link(2, "b", 1, "main", "main")];

        var review = await h.Operations.GetDefaultBranchReviewAsync(WorkspaceId, Context, null, CancellationToken.None);

        Assert.Equal([1], review.Targets.Select(t => t.RepositoryId));
        Assert.Equal(Ids(1, 2), review.OnDefaultBranchRepoIds);
    }

    private static HashSet<int> Ids(params int[] ids) => ids.ToHashSet();

    private static WorkspaceRepositoryLinkListItemDto Link(int repoId, string name, int level, string branch, string defaultBranch, string? tag = null)
        => new(
            WorkspaceRepositoryId: repoId, WorkspaceId: WorkspaceId, RepositoryId: repoId, RepositoryName: name, CloneUrl: "https://x/" + name,
            GitVersion: null, BranchName: branch, CheckedOutTag: tag, DefaultBranchName: defaultBranch,
            OutgoingCommits: 0, IncomingCommits: 0, DefaultBranchBehindCommits: 0, DefaultBranchAheadCommits: 0, BranchHasUpstream: true,
            SyncStatus: RepoSyncStatus.InSync, DependencyLevel: level, Dependencies: 0, UnmatchedDeps: 0, OutOfDateFileRepos: 0,
            RepositoryType: null, HasNewerTag: null, HasSelfFileVersionToken: null, PullRequestState: null, PullRequestNumber: null,
            PullRequestHtmlUrl: null, PullRequestMergedAt: null, PullRequestMergeable: null, PullRequestMergeableState: null,
            PullRequestChangedFiles: null, Archived: false, UncommittedChangedFileCount: 0);

    private sealed class Harness
    {
        public List<string> Calls { get; } = [];
        public FakeUpdateOperations Update { get; }
        public FakePushOperations Push { get; }
        public WorkspaceOperationOutcomeStore Outcomes { get; } = new();
        public bool ContextMissing { get; init; }
        public string? LifecycleState { get; init; }
        public bool IsSpecialWorkspace { get; init; } = true;
        public IReadOnlyList<WorkspaceRepositoryLinkListItemDto> Links { get; set; } = [];
        public int LinkReads { get; private set; }
        public IWorkspaceUpdateAndPushOperations Operations { get; }

        public Harness()
        {
            Update = new FakeUpdateOperations(Calls, Outcomes);
            Push = new FakePushOperations(Calls);
            Operations = new WorkspaceUpdateAndPushOperations(
                Update,
                Push,
                new FakeContextResolver(this),
                LinkProxy.Create(() => { LinkReads++; return Links; }),
                Outcomes,
                new UpdateAndPushApprovalStore(),
                NullLogger<WorkspaceUpdateAndPushOperations>.Instance);
        }

        public Task<UpdateAndPushExecutionResult> Run(UpdateAndPushCommand command)
            => Operations.ExecuteAsync(command, observer: null, CancellationToken.None);
    }

    private sealed class FakeContextResolver(Harness harness) : IWorkspaceFeatureContextResolver
    {
        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Context);

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default)
        {
            if (harness.ContextMissing)
                throw new InvalidOperationException("missing");
            return Task.FromResult(new WorkspaceFeatureContextInfo
            {
                ContextId = contextId,
                WorkspaceId = expectedWorkspaceId ?? WorkspaceId,
                IsSpecialWorkspace = harness.IsSpecialWorkspace,
                LifecycleState = harness.LifecycleState,
            });
        }

        public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFeatureContextInfo>>([]);
    }

    private sealed record PipelineCall(WorkspaceFeatureContextId Context, int? MaxLevel, string? CommitMessage, bool IncludeDeps, bool Restore, string? RunId);

    private sealed record UpdateCall(WorkspaceFeatureContextId Context, int? MaxLevel, IReadOnlySet<int>? RepoIdsToUpdate, string? CommitMessage, bool IncludeDeps, string? RunId);

    private sealed class FakeUpdateOperations(List<string> calls, WorkspaceOperationOutcomeStore outcomes) : IWorkspaceUpdateOperations
    {
        public UpdateAndPushResult Pipeline { get; set; } = UpdateAndPushResult.NotPipelined;
        public DependencyUpdateRunResult Update { get; set; } = DependencyUpdateRunResult.Ok();
        public Exception? ThrowOnPipeline { get; set; }
        public IReadOnlyList<SyncDependenciesRepoPayload> UpdatePlan { get; set; } = [];
        public List<PipelineCall> PipelineCalls { get; } = [];
        public List<UpdateCall> UpdateCalls { get; } = [];
        public int PlanReads { get; private set; }

        private (IReadOnlyList<SyncDependenciesRepoPayload>, bool) CountPlanRead()
        {
            PlanReads++;
            return (UpdatePlan, true);
        }

        public Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlySet<int>? repositoryIds = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CountPlanRead());

        public Task<DependencyUpdateRunResult> UpdateAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken, IProgress<OperationProgress>? progress,
            Action<int, string> setRepositoryError, Action<int, string> setLevelError, IReadOnlySet<int>? repoIdsToUpdate = null,
            string? commitMessage = null, bool includeDepsInCommitMessage = true, int? maxLevel = null, string? runId = null)
        {
            calls.Add("Update");
            UpdateCalls.Add(new UpdateCall(contextId, maxLevel, repoIdsToUpdate, commitMessage, includeDepsInCommitMessage, runId));
            outcomes.Begin(workspaceId, contextId, "Update", runId);
            return Task.FromResult(Update);
        }

        public Task<UpdateAndPushResult> UpdateAndPushAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken, Action<string> reportOverlay,
            Action<int, string> setRepositoryError, Action<int, string> setLevelError, string? commitMessage = null,
            bool includeDepsInCommitMessage = true, int? maxLevel = null, bool restorePackages = true, string? runId = null)
        {
            calls.Add("UpdateAndPush");
            PipelineCalls.Add(new PipelineCall(contextId, maxLevel, commitMessage, includeDepsInCommitMessage, restorePackages, runId));
            outcomes.Begin(workspaceId, contextId, "Update and Push", runId);
            if (ThrowOnPipeline != null)
                throw ThrowOnPipeline;
            return Task.FromResult(Pipeline);
        }

        public Task<int> RestorePackagesAsync(int workspaceId, WorkspaceFeatureContextId contextId, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> RestoreSyncedPackagesAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlySet<int> syncedRepoIds, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            calls.Add("RestoreSynced");
            return Task.FromResult(syncedRepoIds.Count);
        }

        public Task UpdateSingleRepositoryAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, Action<string>? onProgressMessage = null,
            Action<int, string>? onRepoError = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecomputeAndBroadcastWorkspaceSyncedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record PushCall(
        WorkspaceFeatureContextId Context, IReadOnlySet<int> RepositoryIds, bool Synchronized, bool Restore, IReadOnlySet<int>? SyncedRepoIds, string? RunId);

    private sealed class FakePushOperations(List<string> calls) : IWorkspacePushOperations
    {
        public WorkspacePushPlan Plan { get; set; } = new(new HashSet<int> { 1 }, new HashSet<string>(), true);
        public OperationResult PushResult { get; set; } = OperationResult.Ok();
        public bool ThrowNotPossibleWhenSynchronized { get; set; }
        public List<PushCall> PushCalls { get; } = [];
        public List<int?> PlanMaxLevels { get; } = [];

        public Task<WorkspacePushPlan> GetPlanAsync(int workspaceId, WorkspaceFeatureContextId contextId, int? maxLevel = null, CancellationToken cancellationToken = default)
        {
            calls.Add("Plan");
            PlanMaxLevels.Add(maxLevel);
            return Task.FromResult(Plan);
        }

        public Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlySet<int> repositoryIds, CancellationToken cancellationToken = default)
            => Task.FromResult(repositoryIds);

        public Task<OperationResult> PushAsync(
            int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlySet<int> repositoryIds, bool synchronizedPush,
            IReadOnlySet<string> requiredPackageIds, IProgress<OperationProgress>? progress = null, IReadOnlySet<int>? syncedRepoIds = null,
            CancellationToken cancellationToken = default, string? runId = null, bool restorePackages = true)
        {
            calls.Add(synchronizedPush ? "PushSync" : "PushNormal");
            PushCalls.Add(new PushCall(contextId, repositoryIds, synchronizedPush, restorePackages, syncedRepoIds, runId));
            if (synchronizedPush && ThrowNotPossibleWhenSynchronized)
                throw new SynchronizedPushNotPossibleException(2);
            return Task.FromResult(PushResult);
        }

        public Task<OperationResult> PushPendingAsync(int workspaceId, WorkspaceFeatureContextId contextId, bool synchronizedPush, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OperationResult.Ok());

        public Task<OperationResult> PushSingleAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OperationResult.Ok());
    }

    /// <summary>Answers <c>GetAllSnapshotsAsync</c> only; any other call on the link query is a test error.</summary>
    public class LinkProxy : DispatchProxy
    {
        private Func<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> _links = () => [];

        public static IWorkspaceRepositoryLinkListQueryService Create(Func<IReadOnlyList<WorkspaceRepositoryLinkListItemDto>> links)
        {
            var proxy = Create<IWorkspaceRepositoryLinkListQueryService, LinkProxy>();
            ((LinkProxy)(object)proxy)._links = links;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IWorkspaceRepositoryLinkListQueryService.GetAllSnapshotsAsync))
                return Task.FromResult(_links());
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
