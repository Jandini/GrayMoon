using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>
/// Deletes rows that block unlinking or deleting <c>WorkspaceRepositories</c>. SQLite does not
/// support multiple CASCADE paths, so ProjectDependencies and custom-dependency edges must be
/// removed before their parents. Other WRL children are deleted explicitly so bulk
/// <c>ExecuteDeleteAsync</c> never relies on the change tracker or SQLite cascade.
/// </summary>
internal static class WorkspaceRepositoryLinkCleanup
{
    public static async Task DeleteDependentsAsync(
        AppDbContext db,
        IReadOnlyCollection<int> workspaceRepositoryIds,
        IReadOnlyCollection<int> repositoryIds,
        int? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceRepositoryIds.Count == 0 && repositoryIds.Count == 0)
            return;

        if (repositoryIds.Count > 0)
        {
            var projectIdsQuery = db.WorkspaceProjects
                .Where(p => repositoryIds.Contains(p.RepositoryId));
            if (workspaceId is int wsId)
                projectIdsQuery = projectIdsQuery.Where(p => p.WorkspaceId == wsId);

            var projectIds = await projectIdsQuery
                .Select(p => p.ProjectId)
                .ToListAsync(cancellationToken);

            if (projectIds.Count > 0)
            {
                await db.ProjectDependencies
                    .Where(d => projectIds.Contains(d.DependentProjectId) || projectIds.Contains(d.ReferencedProjectId))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            var projectsQuery = db.WorkspaceProjects
                .Where(p => repositoryIds.Contains(p.RepositoryId));
            if (workspaceId is int workspaceFilter)
                projectsQuery = projectsQuery.Where(p => p.WorkspaceId == workspaceFilter);

            await projectsQuery.ExecuteDeleteAsync(cancellationToken);
        }

        if (workspaceRepositoryIds.Count == 0)
            return;

        await db.WorkspaceRepositoryCustomDependencies
            .Where(d => workspaceRepositoryIds.Contains(d.DependentWorkspaceRepositoryId)
                || workspaceRepositoryIds.Contains(d.ReferencedWorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);

        await db.WorkspaceGitChangeEntries
            .Where(e => workspaceRepositoryIds.Contains(e.WorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);

        await db.WorkspaceGitRepositoryStatuses
            .Where(s => workspaceRepositoryIds.Contains(s.WorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);

        await db.RepositoryBranches
            .Where(b => workspaceRepositoryIds.Contains(b.WorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);

        await db.WorkspaceRepositoryPullRequests
            .Where(p => workspaceRepositoryIds.Contains(p.WorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);

        await db.WorkspaceRepositoryActions
            .Where(a => workspaceRepositoryIds.Contains(a.WorkspaceRepositoryId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
