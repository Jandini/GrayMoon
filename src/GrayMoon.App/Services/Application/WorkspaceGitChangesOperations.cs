using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceGitChangesOperations(
    IWorkspaceGitChangesReadService readService,
    IGitChangesWorkerClient workerClient,
    WorkspaceRepository workspaceRepository,
    IWorkspaceContextPathResolver pathResolver) : IWorkspaceGitChangesOperations
{
    public async Task<WorkspaceGitChangesView?> GetAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return null;

        return await readService.GetContextAsync(workspaceId, contextId, cancellationToken);
    }

    public Task<GitChangesCommitResult> CommitAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string commitMessage,
        bool stageAllFirst,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, contextId, repositoryId, cancellationToken, (root, workspaceName, repoName, workspaceRepositoryName) =>
            workerClient.CommitAsync(root, workspaceName, repoName, workspaceRepositoryName, commitMessage, stageAllFirst, cancellationToken));

    public Task<GitChangesMutationResult> StageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, contextId, repositoryId, cancellationToken, (root, workspaceName, repoName, workspaceRepositoryName) =>
            workerClient.StageAsync(root, workspaceName, repoName, workspaceRepositoryName, scope, paths, cancellationToken));

    public Task<GitChangesMutationResult> UnstageAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, contextId, repositoryId, cancellationToken, (root, workspaceName, repoName, workspaceRepositoryName) =>
            workerClient.UnstageAsync(root, workspaceName, repoName, workspaceRepositoryName, scope, paths, cancellationToken));

    private async Task<T> WithResolvedRepo<T>(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken,
        Func<string, string, string, string?, Task<T>> action)
        where T : new()
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Fail<T>("Workspace not found.");

        var link = workspace.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
        var repoName = link?.Repository?.RepositoryName;
        if (string.IsNullOrWhiteSpace(repoName))
            return Fail<T>("Repository is not in the given workspace.");

        var workerArgs = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        var root = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        if (string.IsNullOrWhiteSpace(root))
            return Fail<T>("Workspace root is not configured.");

        return await action(root, workspaceFolderName, repoName, workspaceRepositoryName);
    }

    private static T Fail<T>(string error) where T : new()
    {
        var result = new T();
        switch (result)
        {
            case GitChangesCommitResult commit:
                commit.Success = false;
                commit.ErrorMessage = error;
                break;
            case GitChangesMutationResult mutation:
                mutation.Success = false;
                mutation.ErrorMessage = error;
                break;
        }

        return result;
    }
}
