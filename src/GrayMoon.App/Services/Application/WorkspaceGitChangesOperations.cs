using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceGitChangesOperations(
    IWorkspaceGitChangesReadService readService,
    IGitChangesAgentClient agentClient,
    WorkspaceRepository workspaceRepository,
    WorkspaceService workspaceService) : IWorkspaceGitChangesOperations
{
    public async Task<WorkspaceGitChangesView?> GetAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return null;

        return await readService.GetWorkspaceAsync(workspaceId, cancellationToken);
    }

    public Task<GitChangesCommitResult> CommitAsync(
        int workspaceId,
        int repositoryId,
        string commitMessage,
        bool stageAllFirst,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, repositoryId, cancellationToken, (root, workspaceName, repoName) =>
            agentClient.CommitAsync(root, workspaceName, repoName, commitMessage, stageAllFirst, cancellationToken));

    public Task<GitChangesMutationResult> StageAsync(
        int workspaceId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, repositoryId, cancellationToken, (root, workspaceName, repoName) =>
            agentClient.StageAsync(root, workspaceName, repoName, scope, paths, cancellationToken));

    public Task<GitChangesMutationResult> UnstageAsync(
        int workspaceId,
        int repositoryId,
        GitChangeOperationScope scope,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
        => WithResolvedRepo(workspaceId, repositoryId, cancellationToken, (root, workspaceName, repoName) =>
            agentClient.UnstageAsync(root, workspaceName, repoName, scope, paths, cancellationToken));

    private async Task<T> WithResolvedRepo<T>(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken,
        Func<string, string, string, Task<T>> action)
        where T : new()
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Fail<T>("Workspace not found.");

        var link = workspace.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
        var repoName = link?.Repository?.RepositoryName;
        if (string.IsNullOrWhiteSpace(repoName))
            return Fail<T>("Repository is not in the given workspace.");

        var root = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
            return Fail<T>("Workspace root is not configured.");

        return await action(root, workspace.Name, repoName);
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
