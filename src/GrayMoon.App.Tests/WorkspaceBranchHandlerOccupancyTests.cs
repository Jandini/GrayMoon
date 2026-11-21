using System.Collections.Concurrent;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// B4 item 4: <see cref="WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync"/> must consult
/// <see cref="IWorkspaceBranchOccupancyService"/> before each repo's checkout, skip (never attempt) a repo the
/// branch is not allowed to be checked out on, and still attempt checkout (with <see cref="WorkspaceBranchBulkResult.OccupancyCheckDegraded"/>
/// set) when the occupancy answer itself could not be determined.
/// </summary>
public sealed class WorkspaceBranchHandlerOccupancyTests
{
    private const int WorkspaceId = 1;
    private const string BranchName = "feature/shared";

    [Fact]
    public async Task CheckoutBranchForWorkspaceAsync_skips_repo_occupied_by_another_feature_and_reports_it()
    {
        var occupancy = new FakeBranchOccupancyService();
        occupancy.SetBadge(workspaceRepositoryId: 10, branchName: BranchName, new BranchOccupancyBadge
        {
            Kind = BranchOccupancyKind.Feature,
            FeatureName = "other-feature",
            AllowCheckout = false,
        });

        var branchOperations = new FakeWorkspaceBranchOperations();
        var handler = CreateHandler(branchOperations, occupancy);

        var result = await handler.CheckoutBranchForWorkspaceAsync(
            WorkspaceId,
            new WorkspaceFeatureContextId(1),
            new Dictionary<int, int> { [100] = 10, [101] = 11 },
            BranchName,
            reportProgress: null,
            CancellationToken.None);

        // Repo 100 (occupied) is skipped - never attempted.
        Assert.DoesNotContain(100, branchOperations.AttemptedRepositoryIds);
        Assert.Contains(101, branchOperations.AttemptedRepositoryIds);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Contains("other-feature", result.ErrorsByRepositoryId[100]);
        Assert.False(result.OccupancyCheckDegraded);
    }

    [Fact]
    public async Task CheckoutBranchForWorkspaceAsync_attempts_checkout_and_sets_degraded_flag_when_occupancy_check_fails()
    {
        var occupancy = new FakeBranchOccupancyService();
        occupancy.ThrowFor(workspaceRepositoryId: 10);

        var branchOperations = new FakeWorkspaceBranchOperations();
        var handler = CreateHandler(branchOperations, occupancy);

        var result = await handler.CheckoutBranchForWorkspaceAsync(
            WorkspaceId,
            new WorkspaceFeatureContextId(1),
            new Dictionary<int, int> { [100] = 10 },
            BranchName,
            reportProgress: null,
            CancellationToken.None);

        // Occupancy check failed, so checkout is attempted exactly as before the check existed.
        Assert.Contains(100, branchOperations.AttemptedRepositoryIds);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.OccupancyCheckDegraded);
    }

    [Fact]
    public async Task CheckoutBranchForWorkspaceAsync_checks_out_repo_with_no_occupancy_badge_for_the_branch()
    {
        var occupancy = new FakeBranchOccupancyService();
        var branchOperations = new FakeWorkspaceBranchOperations();
        var handler = CreateHandler(branchOperations, occupancy);

        var result = await handler.CheckoutBranchForWorkspaceAsync(
            WorkspaceId,
            new WorkspaceFeatureContextId(1),
            new Dictionary<int, int> { [100] = 10 },
            BranchName,
            reportProgress: null,
            CancellationToken.None);

        Assert.Contains(100, branchOperations.AttemptedRepositoryIds);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.False(result.OccupancyCheckDegraded);
    }

    [Fact]
    public async Task CheckoutBranchForWorkspaceAsync_refuses_the_whole_call_in_a_Feature_context_without_attempting_any_checkout()
    {
        var branchOperations = new FakeWorkspaceBranchOperations();
        var handler = CreateHandler(branchOperations, new FakeBranchOccupancyService(), new RefuseAllFeatureBranchGuard());

        var result = await handler.CheckoutBranchForWorkspaceAsync(
            WorkspaceId,
            new WorkspaceFeatureContextId(2),
            new Dictionary<int, int> { [100] = 10, [101] = 11 },
            BranchName,
            reportProgress: null,
            CancellationToken.None);

        Assert.Empty(branchOperations.AttemptedRepositoryIds);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.Equal(FeatureBranchPolicy.CreateBranchMessage, result.ErrorsByRepositoryId[100]);
        Assert.Equal(FeatureBranchPolicy.CreateBranchMessage, result.ErrorsByRepositoryId[101]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CheckoutBranchForWorkspaceAsync_runs_occupancy_checks_in_parallel_bounded_by_MaxParallelOperations(int maxParallel)
    {
        var probe = new ConcurrencyProbe();
        var occupancy = new FakeBranchOccupancyService { Probe = probe };
        occupancy.SetBadge(workspaceRepositoryId: 10, branchName: BranchName, new BranchOccupancyBadge
        {
            Kind = BranchOccupancyKind.Feature,
            FeatureName = "other-feature",
            AllowCheckout = false,
        });
        var branchOperations = new FakeWorkspaceBranchOperations();
        var handler = CreateHandler(branchOperations, occupancy, maxParallelOperations: maxParallel);

        var repositories = Enumerable.Range(0, 8).ToDictionary(i => 100 + i, i => 10 + i);
        var result = await handler.CheckoutBranchForWorkspaceAsync(
            WorkspaceId,
            new WorkspaceFeatureContextId(1),
            repositories,
            BranchName,
            reportProgress: null,
            CancellationToken.None);

        // Repo 100 is occupied and skipped; the other seven are checked out, whatever order the checks finished in.
        Assert.Equal(7, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.DoesNotContain(100, branchOperations.AttemptedRepositoryIds);
        Assert.Equal(Enumerable.Range(101, 7), branchOperations.AttemptedRepositoryIds.OrderBy(i => i));
        probe.AssertBoundedBy(maxParallel);
    }

    /// <summary>
    /// The checkout result of a read only repository can carry a warning (its new branch could not be recorded in the
    /// Workspace definition). The handler used to drop it, so the Repositories page never showed it.
    /// </summary>
    [Fact]
    public async Task CheckoutBranchAsync_passes_on_the_warning_of_a_successful_checkout()
    {
        var branchOperations = new FakeWorkspaceBranchOperations
        {
            CheckoutResult = new CheckoutBranchApiResult(true, null) { Warning = "The Workspace definition could not be updated." },
        };
        var handler = CreateHandler(branchOperations, new FakeBranchOccupancyService());

        var (success, error, warning) = await handler.CheckoutBranchAsync(
            WorkspaceId, new WorkspaceFeatureContextId(1), 100, BranchName, isTag: false, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("The Workspace definition could not be updated.", warning);
    }

    [Fact]
    public async Task CheckoutBranchAsync_has_no_warning_for_a_plain_success_or_a_failure()
    {
        var plain = await CreateHandler(new FakeWorkspaceBranchOperations(), new FakeBranchOccupancyService())
            .CheckoutBranchAsync(WorkspaceId, new WorkspaceFeatureContextId(1), 100, BranchName, isTag: false, CancellationToken.None);
        var failed = await CreateHandler(
                new FakeWorkspaceBranchOperations { CheckoutResult = new CheckoutBranchApiResult(false, "boom") { Warning = "ignored" } },
                new FakeBranchOccupancyService())
            .CheckoutBranchAsync(WorkspaceId, new WorkspaceFeatureContextId(1), 100, BranchName, isTag: false, CancellationToken.None);

        Assert.Equal((true, null, null), plain);
        Assert.Equal((false, "boom", null), failed);
    }

    /// <summary>New Branch on one repository: a branch that was already there is a success that says so.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateSingleBranchAsync_says_whether_the_branch_already_existed(bool alreadyExisted)
    {
        var branchOperations = new FakeWorkspaceBranchOperations
        {
            CreateResult = new GrayMoon.App.Models.Api.CreateBranchApiResult { Success = true, AlreadyExisted = alreadyExisted },
        };
        var handler = CreateHandler(branchOperations, new FakeBranchOccupancyService());

        var result = await handler.CreateSingleBranchAsync(
            WorkspaceId, new WorkspaceFeatureContextId(1), 100, BranchName, "main", setUpstream: false, CancellationToken.None);

        Assert.Equal((true, null, alreadyExisted), result);
    }

    private static WorkspaceBranchHandler CreateHandler(
        FakeWorkspaceBranchOperations branchOperations,
        FakeBranchOccupancyService occupancy,
        IFeatureBranchGuard? guard = null,
        int maxParallelOperations = 16)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceBranchOperations>(branchOperations);
        var provider = services.BuildServiceProvider();

        return new WorkspaceBranchHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            branchOperations,
            occupancy,
            guard ?? new AllowAllFeatureBranchGuard(),
            Options.Create(new WorkspaceOptions { MaxParallelOperations = maxParallelOperations }),
            new Policy.AllowAllRepositoryOperationPolicy(),
            NullLogger<WorkspaceBranchHandler>.Instance);
    }

    private sealed class AllowAllFeatureBranchGuard : IFeatureBranchGuard
    {
        public Task<string?> CheckAsync(WorkspaceFeatureContextId contextId, int repositoryId, FeatureBranchAction action, string? target, bool isTag, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class RefuseAllFeatureBranchGuard : IFeatureBranchGuard
    {
        public Task<string?> CheckAsync(WorkspaceFeatureContextId contextId, int repositoryId, FeatureBranchAction action, string? target, bool isTag, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(FeatureBranchPolicy.Evaluate(action, "feature-x", null, target, isTag));
    }

    private sealed class FakeBranchOccupancyService : IWorkspaceBranchOccupancyService
    {
        private readonly Dictionary<(int WorkspaceRepositoryId, string BranchName), BranchOccupancyBadge> _badges = new();
        private readonly HashSet<int> _throwFor = new();

        public ConcurrencyProbe? Probe { get; init; }

        public void SetBadge(int workspaceRepositoryId, string branchName, BranchOccupancyBadge badge) =>
            _badges[(workspaceRepositoryId, branchName)] = badge;

        public void ThrowFor(int workspaceRepositoryId) => _throwFor.Add(workspaceRepositoryId);

        public async Task<IReadOnlyDictionary<string, BranchOccupancyBadge>> GetBadgesForRepositoryAsync(
            int workspaceId,
            int workspaceRepositoryId,
            WorkspaceFeatureContextId viewingContextId,
            CancellationToken cancellationToken = default)
        {
            if (Probe is not null)
                await Probe.RunAsync(cancellationToken);

            if (_throwFor.Contains(workspaceRepositoryId))
                throw new InvalidOperationException("Worker is offline.");

            IReadOnlyDictionary<string, BranchOccupancyBadge> result = _badges
                .Where(kvp => kvp.Key.WorkspaceRepositoryId == workspaceRepositoryId)
                .ToDictionary(kvp => kvp.Key.BranchName, kvp => kvp.Value);
            return result;
        }
    }

    private sealed class FakeWorkspaceBranchOperations : IWorkspaceBranchOperations
    {
        private readonly ConcurrentBag<int> _attemptedRepositoryIds = new();

        public IReadOnlyCollection<int> AttemptedRepositoryIds => _attemptedRepositoryIds;

        public Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, WorkspaceBranchesSnapshot>> GetBranchesForRepositoriesAsync(int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> RefreshBranchesAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CheckoutAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, bool isTag, CancellationToken cancellationToken = default)
        {
            _attemptedRepositoryIds.Add(repositoryId);
            return Task.FromResult(BranchHttpOutcome.Ok(CheckoutResult ?? new CheckoutBranchApiResult(true, null)));
        }

        /// <summary>What a checkout answers. Null means a plain success.</summary>
        public CheckoutBranchApiResult? CheckoutResult { get; init; }

        /// <summary>What a create answers. Null means the fake does not support create.</summary>
        public GrayMoon.App.Models.Api.CreateBranchApiResult? CreateResult { get; init; }

        public Task<BranchHttpOutcome> ReturnToDefaultAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? currentBranchName, bool deleteRemoteBranch, bool allowForceDeleteLocalBranch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CountReposWithLocalBranchAsync(int workspaceId, string? branchName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CreateBranchAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? newBranchName, string? baseBranch, CancellationToken cancellationToken = default) =>
            CreateResult is null ? throw new NotSupportedException() : Task.FromResult(BranchHttpOutcome.Ok(CreateResult));

        public Task<BranchHttpOutcome> SetUpstreamAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> DeleteBranchAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, bool isRemote, bool force, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> UpdateBranchFromDefaultAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
