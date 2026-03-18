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
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool HasUnpushed)> GetPushPlanAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryLink> workspaceRepositories,
        CancellationToken cancellationToken)
    {
        var (payload, _) = await workspacePushService.GetPushPlanAsync(workspaceId, cancellationToken);
        var repoIdsWithUnpushed = workspaceRepositories
            .Where(wr => (wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false)
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        var toPush = payload.Where(p => repoIdsWithUnpushed.Contains(p.RepoId)).ToList();
        return (toPush, toPush.Count > 0);
    }

    public async Task RunPushWithDependenciesAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        Action<string> setProgress,
        Func<Task> refresh,
        Action<string> showToast,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await pushOrchestrator.RunAsync(
                workspaceId,
                repoIds,
                synchronizedPush,
                requiredPackageIds,
                setProgress,
                refresh,
                showToast,
                onAppSideComplete,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push with dependencies failed for workspace {WorkspaceId}", workspaceId);
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

