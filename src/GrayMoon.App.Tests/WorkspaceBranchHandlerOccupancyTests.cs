using System.Collections.Concurrent;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static WorkspaceBranchHandler CreateHandler(
        FakeWorkspaceBranchOperations branchOperations,
        FakeBranchOccupancyService occupancy)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceBranchOperations>(branchOperations);
        var provider = services.BuildServiceProvider();

        return new WorkspaceBranchHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            branchOperations,
            occupancy,
            NullLogger<WorkspaceBranchHandler>.Instance);
    }

    private sealed class FakeBranchOccupancyService : IWorkspaceBranchOccupancyService
    {
        private readonly Dictionary<(int WorkspaceRepositoryId, string BranchName), BranchOccupancyBadge> _badges = new();
        private readonly HashSet<int> _throwFor = new();

        public void SetBadge(int workspaceRepositoryId, string branchName, BranchOccupancyBadge badge) =>
            _badges[(workspaceRepositoryId, branchName)] = badge;

        public void ThrowFor(int workspaceRepositoryId) => _throwFor.Add(workspaceRepositoryId);

        public Task<IReadOnlyDictionary<string, BranchOccupancyBadge>> GetBadgesForRepositoryAsync(
            int workspaceId,
            int workspaceRepositoryId,
            WorkspaceFeatureContextId viewingContextId,
            CancellationToken cancellationToken = default)
        {
            if (_throwFor.Contains(workspaceRepositoryId))
                throw new InvalidOperationException("Worker is offline.");

            IReadOnlyDictionary<string, BranchOccupancyBadge> result = _badges
                .Where(kvp => kvp.Key.WorkspaceRepositoryId == workspaceRepositoryId)
                .ToDictionary(kvp => kvp.Key.BranchName, kvp => kvp.Value);
            return Task.FromResult(result);
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

        public Task<BranchHttpOutcome> RefreshBranchesAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CheckoutAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, bool isTag, CancellationToken cancellationToken = default)
        {
            _attemptedRepositoryIds.Add(repositoryId);
            return Task.FromResult(BranchHttpOutcome.Ok(new CheckoutBranchApiResult(true, null)));
        }

        public Task<BranchHttpOutcome> ReturnToDefaultAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? currentBranchName, bool deleteRemoteBranch, bool allowForceDeleteLocalBranch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CountReposWithLocalBranchAsync(int workspaceId, string? branchName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> CreateBranchAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? newBranchName, string? baseBranch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> SetUpstreamAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> DeleteBranchAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string? branchName, bool isRemote, bool force, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BranchHttpOutcome> UpdateBranchFromDefaultAsync(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
