using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles push-related operations (push plan, push with dependencies, single push with upstream).
/// Stateless; all UI state is owned by the caller.
/// </summary>
public sealed class WorkspacePushHandler(
    PushOrchestrator pushOrchestrator,
    WorkspacePushService workspacePushService,
    ILogger<WorkspacePushHandler> logger)
{
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, IReadOnlySet<int> PushRepoIds, bool HasUnpushed)> GetPushPlanAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryLink> workspaceRepositories,
        CancellationToken cancellationToken,
        int? maxLevel = null)
    {
        var (payload, _) = await workspacePushService.GetPushPlanAsync(workspaceId, cancellationToken);
        var repoIdsWithUnpushed = workspaceRepositories
            .Where(wr => !wr.IsOnTag && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false))
            .Where(wr => !maxLevel.HasValue || (wr.DependencyLevel ?? 0) <= maxLevel.Value)
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        var toPush = payload.Where(p => repoIdsWithUnpushed.Contains(p.RepoId)).ToList();
        var pushRepoIds = toPush.Select(p => p.RepoId).ToHashSet();
        return (toPush, pushRepoIds, toPush.Count > 0);
    }

    public async Task RunPushWithDependenciesAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        Action<string> setProgress,
        Action<string> showToast,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null)
    {
        try
        {
            await pushOrchestrator.RunAsync(
                workspaceId,
                repoIds,
                synchronizedPush,
                requiredPackageIds,
                setProgress,
                showToast,
                onAppSideComplete,
                syncedRepoIds,
                cancellationToken,
                runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is SynchronizedPushNotPossibleException)
                throw;
            logger.LogError(ex, "[PushOrchestrator {RunId}] Push with dependencies failed for workspace {WorkspaceId}", runId, workspaceId);
            showToast(ex.Message);
            throw;
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> PushSingleRepositoryWithUpstreamAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        return await pushOrchestrator.PushSingleAsync(
            workspaceId,
            repositoryId,
            branchName,
            setProgress,
            cancellationToken);
    }
}
