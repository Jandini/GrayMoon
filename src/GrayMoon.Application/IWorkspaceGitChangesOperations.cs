using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.Application;

public interface IWorkspaceGitChangesOperations
{
    Task<WorkspaceGitChangesView?> GetAsync(int workspaceId, CancellationToken cancellationToken);

    Task<GitChangesCommitResult> CommitAsync(
        int workspaceId,
        int repositoryId,
        string commitMessage,
        bool stageAllFirst,
        CancellationToken cancellationToken);

    Task<GitChangesMutationResult> StageAsync(
        int workspaceId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    Task<GitChangesMutationResult> UnstageAsync(
        int workspaceId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);
}
