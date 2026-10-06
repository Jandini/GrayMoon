using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// Application boundary for push. Resolves the workspace's capabilities once per call and selects the push
/// strategy from them; everything below receives the chosen strategy instead of checking the workspace type.
/// </summary>
public sealed class WorkspacePushOperations(
    WorkspacePushHandler pushHandler,
    WorkspaceRepository workspaceRepository,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    WorkspacePushStrategySelector strategySelector) : IWorkspacePushOperations
{
    public async Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        return await GetPlanForLinksAsync(workspaceId, contextId, workspace.Repositories.ToList(), maxLevel, cancellationToken);
    }

    public async Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
            return new HashSet<int>();

        return await workspaceRepository.GetRepositoryIdsNeedingPushAsync(workspaceId, contextId.Value, repositoryIds, cancellationToken);
    }

    public async Task<WorkspacePushPlan> GetPlanForLinksAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        var (_, strategy) = await SelectStrategyAsync(workspaceId, cancellationToken);
        var (_, pushRepoIds, hasUnpushed) = await pushHandler.GetPushPlanAsync(strategy, workspaceId, contextId, links, cancellationToken, maxLevel);
        if (!hasUnpushed || pushRepoIds.Count == 0)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        var required = await strategy.GetRequiredPackageIdsAsync(workspaceId, contextId, pushRepoIds, cancellationToken);
        return new WorkspacePushPlan(pushRepoIds, required, true);
    }

    public async Task<OperationResult> PushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true)
    {
        var (capabilities, strategy) = await SelectStrategyAsync(workspaceId, cancellationToken);
        var run = new WorkspacePushRun(
            workspaceId,
            contextId,
            capabilities,
            repositoryIds,
            synchronizedPush,
            requiredPackageIds,
            syncedRepoIds,
            restorePackages,
            runId);
        return await pushHandler.RunPushWithDependenciesAsync(strategy, run, progress, cancellationToken: cancellationToken);
    }

    public async Task<OperationResult> PushPendingAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(workspaceId, contextId, maxLevel: null, cancellationToken);
        if (!plan.HasUnpushed)
            return OperationResult.Ok();

        return await PushAsync(
            workspaceId,
            contextId,
            plan.RepositoryIds,
            synchronizedPush,
            plan.RequiredPackageIds,
            progress,
            cancellationToken: cancellationToken);
    }

    public Task<OperationResult> PushSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => pushHandler.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            contextId,
            repositoryId,
            branchName,
            progress,
            cancellationToken);

    private async Task<(WorkspaceCapabilities Capabilities, IWorkspacePushStrategy Strategy)> SelectStrategyAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);
        return (capabilities, strategySelector.Select(capabilities));
    }
}
