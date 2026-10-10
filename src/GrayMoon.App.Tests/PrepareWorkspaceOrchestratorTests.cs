using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Prepare Workspace's branch gate and mode routing. Any branch failure, unknown outcome, exception or cancellation
/// stops the workflow before the update, the two-lane pipeline or the push; once every targeted repository is on the
/// new branch, each option combination runs exactly its own existing workflow.
/// </summary>
public sealed class PrepareWorkspaceOrchestratorTests
{
    private const string Branch = "feature/prep";
    private const int RepoA = 1;
    private const int RepoB = 2;
    private const int RepoC = 3;

    public static TheoryData<bool, bool> AllModes => new()
    {
        { false, false },
        { true, false },
        { false, true },
        { true, true },
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task One_failed_branch_stops_every_later_phase(bool update, bool push)
    {
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA, RepoB], errors: new() { [RepoC] = "fatal: a branch named 'feature/prep' already exists" }),
        };
        var errors = new RecordedErrors();

        var result = await RunAsync(phases, errors, update, push, requested: Set(RepoA, RepoB, RepoC));

        Assert.False(result.BranchesCreated);
        Assert.False(result.Success);
        Assert.False(result.PushPending);
        Assert.Null(result.Update);
        Assert.Null(result.Pipeline);
        Assert.Equal([RepoC], result.BranchErrors.Keys);
        Assert.Equal(1, phases.BranchCalls);
        Assert.Equal(0, phases.UpdateCalls);
        Assert.Equal(0, phases.PipelineCalls);
        Assert.Contains("already exists", errors.Repository[RepoC]);
        Assert.False(errors.Repository.ContainsKey(RepoA));
        Assert.False(errors.Repository.ContainsKey(RepoB));
        Assert.Equal(PrepareWorkspaceResult.BranchesStoppedMessage, errors.Level[0]);
    }

    [Fact]
    public async Task Every_branch_failure_is_reported()
    {
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA], errors: new() { [RepoB] = "b failed", [RepoC] = "c failed" }),
        };
        var errors = new RecordedErrors();

        var result = await RunAsync(phases, errors, update: true, push: true, requested: Set(RepoA, RepoB, RepoC));

        Assert.False(result.BranchesCreated);
        Assert.Equal(new HashSet<int> { RepoB, RepoC }, result.BranchErrors.Keys.ToHashSet());
        Assert.Equal("b failed", errors.Repository[RepoB]);
        Assert.Equal("c failed", errors.Repository[RepoC]);
        Assert.Equal(0, phases.UpdateCalls + phases.PipelineCalls);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Branch_creation_exception_propagates_and_nothing_else_runs(bool update, bool push)
    {
        var phases = new FakePhases { BranchException = new InvalidOperationException("Worker not connected.") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(phases, new RecordedErrors(), update, push, requested: null));

        Assert.Equal("Worker not connected.", ex.Message);
        Assert.Equal(0, phases.UpdateCalls + phases.PipelineCalls);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Cancelled_branch_creation_propagates_and_nothing_else_runs(bool update, bool push)
    {
        using var cts = new CancellationTokenSource();
        var phases = new FakePhases
        {
            OnBranches = () =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(phases, new RecordedErrors(), update, push, requested: null, cts.Token));

        Assert.Equal(0, phases.UpdateCalls + phases.PipelineCalls);
    }

    [Fact]
    public async Task Cancellation_after_every_branch_succeeded_still_stops_before_the_update()
    {
        using var cts = new CancellationTokenSource();
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA, RepoB]),
            OnBranches = cts.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: null, cts.Token));

        Assert.Equal(0, phases.UpdateCalls + phases.PipelineCalls);
    }

    [Fact]
    public async Task Attempted_repository_without_an_outcome_fails_closed()
    {
        var phases = new FakePhases
        {
            Branches = new BranchCreationOutcome(Set(RepoA, RepoB), Checked((RepoA, Branch)), new Dictionary<int, string>()),
        };
        var errors = new RecordedErrors();

        var result = await RunAsync(phases, errors, update: true, push: false, requested: null);

        Assert.False(result.BranchesCreated);
        Assert.Equal([RepoB], result.BranchErrors.Keys);
        Assert.True(errors.Repository.ContainsKey(RepoB));
        Assert.Equal(0, phases.UpdateCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    public async Task Success_without_the_new_branch_checked_out_fails_closed(string? checkedOut)
    {
        var phases = new FakePhases
        {
            Branches = new BranchCreationOutcome(Set(RepoA, RepoB), Checked((RepoA, Branch), (RepoB, checkedOut)), new Dictionary<int, string>()),
        };

        var result = await RunAsync(phases, new RecordedErrors(), update: false, push: true, requested: null);

        Assert.False(result.BranchesCreated);
        Assert.Equal([RepoB], result.BranchErrors.Keys);
        Assert.False(result.PushPending);
    }

    [Fact]
    public async Task Requested_repository_that_was_never_attempted_fails()
    {
        var phases = new FakePhases { Branches = Outcome(created: [RepoA]) };

        var result = await RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: Set(RepoA, RepoB));

        Assert.False(result.BranchesCreated);
        Assert.Equal([RepoB], result.BranchErrors.Keys);
        Assert.Equal(0, phases.UpdateCalls + phases.PipelineCalls);
    }

    [Fact]
    public async Task Repository_skipped_on_a_tag_is_not_a_branch_failure()
    {
        // RepoC is tag-pinned and left out of the selection (Skip Repos on Tags); the update and push skip tag-pinned repos.
        var phases = new FakePhases { Branches = Outcome(created: [RepoA, RepoB]) };

        var result = await RunAsync(phases, new RecordedErrors(), update: true, push: false, requested: Set(RepoA, RepoB));

        Assert.True(result.BranchesCreated);
        Assert.True(result.Success);
        Assert.Equal(Set(RepoA, RepoB), phases.RequestedRepositoryIds);
        Assert.Equal(1, phases.UpdateCalls);
    }

    [Fact]
    public async Task Empty_selection_creates_nothing_and_is_not_a_success()
    {
        var phases = new FakePhases();
        var errors = new RecordedErrors();

        var result = await RunAsync(phases, errors, update: true, push: true, requested: new HashSet<int>());

        Assert.False(result.Success);
        Assert.Equal(0, phases.BranchCalls + phases.UpdateCalls + phases.PipelineCalls);
        Assert.Equal(PrepareWorkspaceOrchestrator.NoRepositoriesMessage, errors.Level[0]);
    }

    [Fact]
    public async Task No_targeted_repository_is_not_a_success()
    {
        var phases = new FakePhases { Branches = BranchCreationOutcome.Empty };

        var result = await RunAsync(phases, new RecordedErrors(), update: false, push: true, requested: null);

        Assert.False(result.Success);
        Assert.False(result.PushPending);
    }

    [Theory]
    [InlineData(false, false, 0, 0, false)]
    [InlineData(true, false, 1, 0, false)]
    [InlineData(false, true, 0, 0, true)]
    [InlineData(true, true, 0, 1, false)]
    public async Task Each_mode_runs_its_own_workflow_after_the_gate(bool update, bool push, int updateCalls, int pipelineCalls, bool pushPending)
    {
        var phases = new FakePhases { Branches = Outcome(created: [RepoA, RepoB, RepoC]) };

        var result = await RunAsync(phases, new RecordedErrors(), update, push, requested: Set(RepoA, RepoB, RepoC));

        Assert.True(result.BranchesCreated);
        Assert.True(result.Success);
        Assert.Equal(updateCalls, phases.UpdateCalls);
        Assert.Equal(pipelineCalls, phases.PipelineCalls);
        Assert.Equal(pushPending, result.PushPending);
        Assert.Equal(update && push, result.Pipeline != null);
    }

    [Fact]
    public async Task Pipelined_run_reports_both_lanes_and_hands_nothing_back()
    {
        var pushErrors = new Dictionary<int, string> { [RepoB] = "rejected" };
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA, RepoB]),
            Pipeline = new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(Set(RepoA)), OperationResult.Fail("push failed", pushErrors), PushedRepoCount: 1),
            OnPipeline = overlay => overlay("Pushing Level 1"),
        };
        var progress = new List<string>();

        var result = await RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: null, progress: progress);

        Assert.True(result.BranchesCreated);
        Assert.False(result.Success);
        Assert.False(result.PushPending);
        Assert.True(result.Pipeline!.Update.Success);
        Assert.False(result.Pipeline.Push!.Success);
        Assert.Equal(Set(RepoA), result.SyncedRepoIds);
        Assert.Equal(0, phases.UpdateCalls);
        Assert.Equal(["Creating branches...", "Pushing Level 1"], progress);
    }

    [Fact]
    public async Task Not_pipelined_runs_the_update_once_and_hands_the_push_back()
    {
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA]),
            Pipeline = UpdateAndPushResult.NotPipelined,
            Update = DependencyUpdateRunResult.Ok(Set(RepoA)),
        };

        var result = await RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: null);

        Assert.Equal(1, phases.PipelineCalls);
        Assert.Equal(1, phases.UpdateCalls);
        Assert.Null(result.Pipeline);
        Assert.True(result.PushPending);
        Assert.Equal(Set(RepoA), result.SyncedRepoIds);
    }

    [Fact]
    public async Task Not_pipelined_with_a_failed_update_does_not_push()
    {
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA]),
            Pipeline = UpdateAndPushResult.NotPipelined,
            Update = DependencyUpdateRunResult.Failed(),
        };

        var result = await RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: null);

        Assert.False(result.Success);
        Assert.False(result.PushPending);
    }

    [Fact]
    public async Task Pipeline_exception_propagates_without_a_fallback_update()
    {
        var phases = new FakePhases
        {
            Branches = Outcome(created: [RepoA]),
            PipelineException = new InvalidOperationException("lane failed"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(phases, new RecordedErrors(), update: true, push: true, requested: null));

        Assert.Equal(0, phases.UpdateCalls);
    }

    private static Task<PrepareWorkspaceResult> RunAsync(
        FakePhases phases,
        RecordedErrors errors,
        bool update,
        bool push,
        IReadOnlySet<int>? requested,
        CancellationToken cancellationToken = default,
        List<string>? progress = null)
    {
        var orchestrator = new PrepareWorkspaceOrchestrator(phases, NullLogger<PrepareWorkspaceOrchestrator>.Instance);
        return orchestrator.RunAsync(
            workspaceId: 1,
            new WorkspaceFeatureContextId(1),
            Branch,
            "__default__",
            requested,
            update,
            push,
            commitMessage: null,
            progress == null ? null : new SynchronousProgress(progress.Add),
            errors.SetRepository,
            errors.SetLevel,
            cancellationToken);
    }

    private static HashSet<int> Set(params int[] ids) => [.. ids];

    private static Dictionary<int, string?> Checked(params (int RepoId, string? Branch)[] entries)
        => entries.ToDictionary(e => e.RepoId, e => e.Branch);

    private static BranchCreationOutcome Outcome(int[] created, Dictionary<int, string>? errors = null)
    {
        errors ??= [];
        return new BranchCreationOutcome(
            created.Concat(errors.Keys).ToHashSet(),
            created.ToDictionary(id => id, _ => (string?)Branch),
            errors);
    }

    private sealed class RecordedErrors
    {
        public Dictionary<int, string> Repository { get; } = [];
        public Dictionary<int, string> Level { get; } = [];

        public void SetRepository(int repoId, string message) => Repository[repoId] = message;

        public void SetLevel(int level, string message) => Level[level] = message;
    }

    private sealed class FakePhases : IPrepareWorkspacePhases
    {
        public BranchCreationOutcome Branches { get; init; } = BranchCreationOutcome.Empty;
        public Exception? BranchException { get; init; }
        public Action? OnBranches { get; init; }
        public DependencyUpdateRunResult Update { get; init; } = DependencyUpdateRunResult.Ok();
        public UpdateAndPushResult Pipeline { get; init; } = new(true, DependencyUpdateRunResult.Ok(), OperationResult.Ok());
        public Exception? PipelineException { get; init; }
        public Action<Action<string>>? OnPipeline { get; init; }

        public int BranchCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int PipelineCalls { get; private set; }
        public IReadOnlySet<int>? RequestedRepositoryIds { get; private set; }

        public Task<BranchCreationOutcome> CreateBranchesAsync(
            int workspaceId,
            WorkspaceFeatureContextId contextId,
            string newBranchName,
            string baseBranch,
            IReadOnlySet<int>? repositoryIds,
            IProgress<OperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            BranchCalls++;
            RequestedRepositoryIds = repositoryIds;
            OnBranches?.Invoke();
            if (BranchException != null)
                throw BranchException;
            return Task.FromResult(Branches);
        }

        public Task<DependencyUpdateRunResult> UpdateDependenciesAsync(
            int workspaceId,
            WorkspaceFeatureContextId contextId,
            string? commitMessage,
            IProgress<OperationProgress>? progress,
            Action<int, string> setRepositoryError,
            Action<int, string> setLevelError,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return Task.FromResult(Update);
        }

        public Task<UpdateAndPushResult> UpdateAndPushAsync(
            int workspaceId,
            WorkspaceFeatureContextId contextId,
            string? commitMessage,
            Action<string> reportOverlay,
            Action<int, string> setRepositoryError,
            Action<int, string> setLevelError,
            CancellationToken cancellationToken)
        {
            PipelineCalls++;
            OnPipeline?.Invoke(reportOverlay);
            if (PipelineException != null)
                throw PipelineException;
            return Task.FromResult(Pipeline);
        }
    }
}
