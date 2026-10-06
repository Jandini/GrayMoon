using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Applies a workspace profile change and retires .NET-derived rows when the workspace can no longer
/// use them. Callers persist the three axes; this owns the lifecycle, not the modal.
/// </summary>
public static class WorkspaceProfileTransition
{
    /// <summary>Same freeze as rename and root: type is a workspace-wide setting Features inherit.</summary>
    public const string TypeChangeBlockedByFeaturesMessage =
        "Rename and root changes are not possible while Features exist. Remove Features first.";

    public static async Task ApplyAsync(
        AppDbContext db,
        Workspace workspace,
        WorkspaceType type,
        WorkspaceVersioningMode versioningMode,
        WorkspaceCiProvider ciProvider,
        CancellationToken cancellationToken = default)
    {
        var previous = new WorkspaceCapabilities(workspace.Type, workspace.VersioningMode, workspace.CiProvider);
        var next = new WorkspaceCapabilities(type, versioningMode, ciProvider);

        if (previous.Type != next.Type
            && await db.WorkspaceFeatures.AnyAsync(feature => feature.WorkspaceId == workspace.WorkspaceId, cancellationToken))
        {
            throw new InvalidOperationException(TypeChangeBlockedByFeaturesMessage);
        }

        workspace.Type = type;
        workspace.VersioningMode = versioningMode;
        workspace.CiProvider = ciProvider;

        // Versioning off leaves persisted GitVersion and {@Repo} configs in place: readers already treat
        // them as not applicable. CI off leaves Actions rows; the CI provider is the only consumer.
        if (previous.DiscoversDotNetProjects && !next.DiscoversDotNetProjects)
            await ClearDotNetDerivedStateAsync(db, workspace.WorkspaceId, cancellationToken);
    }

    public static async Task ClearDotNetDerivedStateAsync(
        AppDbContext db,
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var projectIds = await db.WorkspaceProjects
            .Where(project => project.WorkspaceId == workspaceId)
            .Select(project => project.ProjectId)
            .ToListAsync(cancellationToken);

        if (projectIds.Count > 0)
        {
            var edges = await db.ProjectDependencies
                .Where(edge => projectIds.Contains(edge.DependentProjectId) || projectIds.Contains(edge.ReferencedProjectId))
                .ToListAsync(cancellationToken);
            if (edges.Count > 0)
                db.ProjectDependencies.RemoveRange(edges);

            var projects = await db.WorkspaceProjects
                .Where(project => project.WorkspaceId == workspaceId)
                .ToListAsync(cancellationToken);
            db.WorkspaceProjects.RemoveRange(projects);
        }

        await db.WorkspaceRepositories
            .Where(link => link.WorkspaceId == workspaceId)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(link => link.DependencyLevel, (int?)null)
                    .SetProperty(link => link.UnmatchedDeps, (int?)null)
                    .SetProperty(link => link.Dependencies, (int?)null)
                    .SetProperty(link => link.RepositoryType, (ProjectType?)null),
                cancellationToken);

        var linkIds = await db.WorkspaceRepositories
            .Where(link => link.WorkspaceId == workspaceId)
            .Select(link => link.WorkspaceRepositoryId)
            .ToListAsync(cancellationToken);

        if (linkIds.Count > 0)
        {
            await db.WorkspaceRepositoryContextStates
                .Where(state => linkIds.Contains(state.WorkspaceRepositoryId))
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(state => state.DependencyLevel, (int?)null)
                        .SetProperty(state => state.UnmatchedDeps, (int?)null)
                        .SetProperty(state => state.Dependencies, (int?)null)
                        .SetProperty(state => state.RepositoryType, (ProjectType?)null),
                    cancellationToken);
        }
    }
}
