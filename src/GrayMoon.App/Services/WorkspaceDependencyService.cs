using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>
/// Owns push-dependency business logic: push plan payload, push dependency info, and the rule for when to show the synchronized-push modal.
/// All dependency-related data and decisions go through this service; decouples dependency from UI and push execution.
/// </summary>
public sealed class WorkspaceDependencyService(WorkspaceProjectRepository workspaceProjectRepository)
{
    /// <summary>Returns push plan: all workspace repos with dependency level and required packages. Used for dependency-synchronized push and filtering.</summary>
    public async Task<IReadOnlyList<PushRepoPayload>> GetPushPlanPayloadAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await workspaceProjectRepository.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
    }

    /// <summary>Gets push dependency info for a single repo (e.g. badge click). Returns null if repo not in workspace.</summary>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        return await workspaceProjectRepository.GetPushDependencyInfoForRepoAsync(workspaceId, repositoryId, cancellationToken);
    }

    /// <summary>Gets push dependency info for a set of repos (merged required packages and dependency path). Used for main Push button.</summary>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoSetAsync(int workspaceId, IReadOnlySet<int> repoIds, CancellationToken cancellationToken = default)
    {
        return await workspaceProjectRepository.GetPushDependencyInfoForRepoSetAsync(workspaceId, repoIds, cancellationToken);
    }

    /// <summary>Returns true when the synchronized-push modal should be shown: at least one dependency repo needs to be pushed.</summary>
    public static bool ShouldShowSynchronizedPushModal(PushDependencyInfoForRepo? depInfo, IReadOnlySet<int> repoIdsThatNeedPush)
    {
        if (depInfo == null || repoIdsThatNeedPush == null || repoIdsThatNeedPush.Count == 0)
            return false;
        return depInfo.DependencyRepoIds.Any(id => repoIdsThatNeedPush.Contains(id));
    }
}
