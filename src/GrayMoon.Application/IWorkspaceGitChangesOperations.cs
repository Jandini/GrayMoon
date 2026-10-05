using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;

namespace GrayMoon.Application;

public interface IWorkspaceGitChangesOperations
{
    Task<WorkspaceGitChangesView?> GetAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken);

    Task<GitChangesCommitResult> CommitAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string commitMessage,
        bool stageAllFirst,
        CancellationToken cancellationToken);

    Task<GitChangesMutationResult> StageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    Task<GitChangesMutationResult> UnstageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);
}
