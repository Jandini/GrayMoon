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
    WorkspaceRepository workspaceRepository,
    ILogger<WorkspacePushHandler> logger)
{
    /// <summary>
    /// Builds the push plan scoped to <paramref name="contextId"/>: payload/levels come from
    /// <paramref name="strategy"/> for that context (a dependency-aware strategy reads that context's own
    /// dependency graph), and "needs push" comes from that context's own <see cref="WorkspaceRepositoryContextState"/>
    /// row per repo (<see cref="WorkspaceRepository.GetRepositoryIdsNeedingPushAsync"/>) instead of the shared
    /// <see cref="WorkspaceRepositoryLink"/> fields on <paramref name="workspaceRepositories"/> - a Feature's
    /// push plan must never be computed from the Workspace's own commit counts/dependency level (see
    /// AGENTS.md "Feature-context scoping").
    /// </summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, IReadOnlySet<int> PushRepoIds, bool HasUnpushed)> GetPushPlanAsync(
        IWorkspacePushStrategy strategy,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<WorkspaceRepositoryLink> workspaceRepositories,
        CancellationToken cancellationToken,
        int? maxLevel = null)
    {
        var payload = await strategy.GetPayloadAsync(workspaceId, contextId, cancellationToken);
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
        IWorkspacePushStrategy strategy,
        WorkspacePushRun run,
        IProgress<OperationProgress>? progress = null,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await pushOrchestrator.RunAsync(strategy, run, progress, onAppSideComplete, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is SynchronizedPushNotPossibleException)
                throw;
            logger.LogError(ex, "[PushOrchestrator {RunId}] Push with dependencies failed for workspace {WorkspaceId}", run.RunId, run.WorkspaceId);
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
