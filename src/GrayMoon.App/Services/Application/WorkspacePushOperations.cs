using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspacePushOperations(
    WorkspacePushHandler pushHandler,
    WorkspaceRepository workspaceRepository,
    WorkspaceDependencyService dependencyService) : IWorkspacePushOperations
{
    public async Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        return await GetPlanForLinksAsync(workspaceId, workspace.Repositories.ToList(), maxLevel, cancellationToken);
    }

    public async Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
            return new HashSet<int>();

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new HashSet<int>();

        return workspace.Repositories
            .Where(wr => repositoryIds.Contains(wr.RepositoryId)
                && !wr.IsOnTag
                && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
    }

    public async Task<WorkspacePushPlan> GetPlanForLinksAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        var (_, pushRepoIds, hasUnpushed) = await pushHandler.GetPushPlanAsync(workspaceId, links, cancellationToken, maxLevel);
        if (!hasUnpushed || pushRepoIds.Count == 0)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        var depInfo = await dependencyService.GetPushDependencyInfoForRepoSetAsync(workspaceId, pushRepoIds, cancellationToken);
        var required = depInfo?.PayloadForRepo?.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new WorkspacePushPlan(pushRepoIds, required, true);
    }

    public Task<OperationResult> PushAsync(
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
        => pushHandler.RunPushWithDependenciesAsync(
            workspaceId,
            contextId,
            repositoryIds,
            synchronizedPush,
            requiredPackageIds,
            progress,
            syncedRepoIds: syncedRepoIds,
            cancellationToken: cancellationToken,
            runId: runId,
            restorePackages: restorePackages);

    public async Task<OperationResult> PushPendingAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(workspaceId, maxLevel: null, cancellationToken);
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
}
