using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services.Application;

public sealed record WorkspacePushPlan(
    IReadOnlySet<int> RepositoryIds,
    IReadOnlySet<string> RequiredPackageIds,
    bool HasUnpushed);

public interface IWorkspacePushOperations
{
    Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        int? maxLevel = null,
        CancellationToken cancellationToken = default);

    Task<WorkspacePushPlan> GetPlanFromStoreAsync(
        int workspaceId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PushAsync(
        int workspaceId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null);

    Task<OperationResult> PushPendingAsync(
        int workspaceId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PushSingleAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspacePushOperations(
    WorkspacePushHandler pushHandler,
    WorkspaceRepository workspaceRepository,
    WorkspaceDependencyService dependencyService) : IWorkspacePushOperations
{
    public async Task<WorkspacePushPlan> GetPlanAsync(
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

    public async Task<WorkspacePushPlan> GetPlanFromStoreAsync(
        int workspaceId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        return await GetPlanAsync(workspaceId, workspace.Repositories.ToList(), maxLevel, cancellationToken);
    }

    public Task<OperationResult> PushAsync(
        int workspaceId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null)
        => pushHandler.RunPushWithDependenciesAsync(
            workspaceId,
            repositoryIds,
            synchronizedPush,
            requiredPackageIds,
            progress,
            syncedRepoIds: syncedRepoIds,
            cancellationToken: cancellationToken,
            runId: runId);

    public async Task<OperationResult> PushPendingAsync(
        int workspaceId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanFromStoreAsync(workspaceId, maxLevel: null, cancellationToken);
        if (!plan.HasUnpushed)
            return OperationResult.Ok();

        return await PushAsync(
            workspaceId,
            plan.RepositoryIds,
            synchronizedPush,
            plan.RequiredPackageIds,
            progress,
            cancellationToken: cancellationToken);
    }

    public Task<OperationResult> PushSingleAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => pushHandler.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            repositoryId,
            branchName,
            progress,
            cancellationToken);
}
