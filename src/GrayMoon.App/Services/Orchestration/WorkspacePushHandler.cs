using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles push-related operations (push plan, push with dependencies, single push with upstream).
/// Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class WorkspacePushHandler(
    PushOrchestrator pushOrchestrator,
    WorkspacePushService workspacePushService,
    WorkspaceRepository workspaceRepository,
    ILogger<WorkspacePushHandler> logger)
{
    /// <summary>
    /// Builds the push plan scoped to <paramref name="contextId"/>: payload/levels come from that context's own
    /// dependency graph (<see cref="WorkspacePushService.GetPushPlanAsync(int,int,CancellationToken)"/>), and
    /// "needs push" comes from that context's own <see cref="WorkspaceRepositoryContextState"/> row per repo
    /// (<see cref="WorkspaceRepository.GetRepositoryIdsNeedingPushAsync"/>) instead of the shared
    /// <see cref="WorkspaceRepositoryLink"/> fields on <paramref name="workspaceRepositories"/> - a Feature's
    /// push plan must never be computed from the Workspace's own commit counts/dependency level (see
    /// AGENTS.md "Feature-context scoping").
    /// </summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, IReadOnlySet<int> PushRepoIds, bool HasUnpushed)> GetPushPlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<WorkspaceRepositoryLink> workspaceRepositories,
        CancellationToken cancellationToken,
        int? maxLevel = null)
    {
        var (payload, _) = await workspacePushService.GetPushPlanAsync(workspaceId, contextId.Value, cancellationToken);
        var allRepoIds = workspaceRepositories.Select(wr => wr.RepositoryId).ToHashSet();
        var needingPush = await workspaceRepository.GetRepositoryIdsNeedingPushAsync(workspaceId, contextId.Value, allRepoIds, cancellationToken);
        var levelByRepo = payload.ToDictionary(p => p.RepoId, p => p.DependencyLevel);
        var repoIdsWithUnpushed = needingPush
            .Where(id => !maxLevel.HasValue || (levelByRepo.GetValueOrDefault(id) ?? 0) <= maxLevel.Value)
            .ToHashSet();
        var toPush = payload.Where(p => repoIdsWithUnpushed.Contains(p.RepoId)).ToList();
        var pushRepoIds = toPush.Select(p => p.RepoId).ToHashSet();
        return (toPush, pushRepoIds, toPush.Count > 0);
    }

    public async Task<OperationResult> RunPushWithDependenciesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true)
    {
        try
        {
            return await pushOrchestrator.RunAsync(
                workspaceId,
                contextId,
                repoIds,
                synchronizedPush,
                requiredPackageIds,
                progress,
                onAppSideComplete,
                syncedRepoIds,
                cancellationToken,
                runId,
                restorePackages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is SynchronizedPushNotPossibleException)
                throw;
            logger.LogError(ex, "[PushOrchestrator {RunId}] Push with dependencies failed for workspace {WorkspaceId}", runId, workspaceId);
            throw;
        }
    }

    public Task<OperationResult> PushSingleRepositoryWithUpstreamAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return pushOrchestrator.PushSingleAsync(
            workspaceId,
            contextId,
            repositoryId,
            branchName,
            progress,
            cancellationToken);
    }
}
