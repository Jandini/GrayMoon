using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>One repository of a workspace context, with everything the Worker needs to resolve its folder.</summary>
public sealed record MonitorTarget(
    string Root,
    string WorkspaceName,
    string RepositoryName,
    string? WorkspaceRepositoryName,
    int WorkspaceId,
    int RepositoryId,
    string RepositoryPath);

/// <summary>
/// Resolves the repositories of one workspace context into <see cref="MonitorTarget"/>s. Shared by the status
/// scanner and the watcher renewal so both address exactly the same folders.
/// </summary>
public static class GitChangesMonitorTargets
{
    public static async Task<List<MonitorTarget>> ResolveAsync(
        AppDbContext dbContext,
        IWorkspaceContextPathResolver pathResolver,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int>? repositoryIds,
        CancellationToken cancellationToken)
    {
        var linksQuery = dbContext.WorkspaceRepositories
            .Where(l => l.WorkspaceId == workspaceId);
        if (repositoryIds is not null)
        {
            var idList = repositoryIds.Distinct().ToList();
            linksQuery = linksQuery.Where(l => idList.Contains(l.RepositoryId));
        }

        var links = await linksQuery
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var workerArgs = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        var root = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;

        var targets = new List<MonitorTarget>();
        var scannable = string.IsNullOrWhiteSpace(root)
            ? new List<WorkspaceRepositoryLink>()
            : links.Where(l => l.Workspace != null && l.Repository != null).ToList();
        var repositoryPaths = await pathResolver.GetRepositoryPathsAsync(
            contextId, scannable.Select(l => l.WorkspaceRepositoryId).ToList(), cancellationToken);
        foreach (var link in scannable)
        {
            var repositoryPath = repositoryPaths[link.WorkspaceRepositoryId];
            targets.Add(new MonitorTarget(
                root,
                workspaceFolderName,
                link.Repository!.RepositoryName,
                workspaceRepositoryName,
                link.WorkspaceId,
                link.RepositoryId,
                repositoryPath));
        }

        return targets;
    }
}
