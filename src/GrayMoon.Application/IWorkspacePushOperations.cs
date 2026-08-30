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
