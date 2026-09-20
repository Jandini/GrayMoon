using GrayMoon.Application.Features;

namespace GrayMoon.Application;

public sealed record WorkspacePushPlan(
    IReadOnlySet<int> RepositoryIds,
    IReadOnlySet<string> RequiredPackageIds,
    bool HasUnpushed);

public interface IWorkspacePushOperations
{
    Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lightweight check (no dependency-package lookup) for which of the given repositories have unpushed commits or a branch never pushed upstream.</summary>
    Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true);

    Task<OperationResult> PushPendingAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PushSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
