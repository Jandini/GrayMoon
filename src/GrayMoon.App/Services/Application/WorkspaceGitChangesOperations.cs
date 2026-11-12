using GrayMoon.App.Data;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// Git Changes operations for one repository. The page runs these for many repositories at once (Commit All, Stage All),
/// so everything here must be safe to call in parallel on the one instance of the page's scope. Each call therefore reads
/// what it needs through its own short-lived <see cref="AppDbContext"/> from the factory. The scoped
/// <c>WorkspaceRepository</c> owns a single context, which EF Core does not allow two overlapping queries on; using it here
/// made parallel commits throw "A second operation was started on this context instance", and the repository whose call lost
/// the race was never sent to the Worker.
/// </summary>
public sealed class WorkspaceGitChangesOperations(
    IWorkspaceGitChangesReadService readService,
    IGitChangesWorkerClient workerClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceRepositoryOperationPolicy operationPolicy) : IWorkspaceGitChangesOperations
{
    public async Task<WorkspaceGitChangesView?> GetAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var exists = await db.Workspaces.AsNoTracking()
                .AnyAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
            if (!exists)
                return null;
        }

        return await readService.GetContextAsync(workspaceId, contextId, cancellationToken);
    }

    /// <summary>
    /// Commits in one repository. A read-only repository is refused here, so the page, the REST endpoint and any
    /// later caller get the same answer. Staging and unstaging in it stay possible: they change nothing that is shared.
    /// </summary>
    public async Task<GitChangesCommitResult> CommitAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string commitMessage,
        bool stageAllFirst,
        CancellationToken cancellationToken)
    {
        var decision = await operationPolicy.EvaluateAsync(
            workspaceId, contextId, RepositoryOperation.CommitGitChanges, repositoryId, cancellationToken: cancellationToken);
        if (!decision.IsAllowed)
            return Fail<GitChangesCommitResult>(decision.Message ?? "The repository cannot be committed.");

        return await WithResolvedRepo(workspaceId, contextId, repositoryId, cancellationToken, (root, workspaceName, repoName, workspaceRepositoryName) =>
            workerClient.CommitAsync(root, workspaceName, repoName, workspaceRepositoryName, commitMessage, stageAllFirst, cancellationToken));
    }

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
        bool workspaceExists;
        string? repoName;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            workspaceExists = await db.Workspaces.AsNoTracking()
                .AnyAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
            repoName = workspaceExists
                ? await db.WorkspaceRepositories.AsNoTracking()
                    .Where(l => l.WorkspaceId == workspaceId && l.RepositoryId == repositoryId && l.Repository != null)
                    .Select(l => l.Repository!.RepositoryName)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
        }

        if (!workspaceExists)
            return Fail<T>("Workspace not found.");

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
