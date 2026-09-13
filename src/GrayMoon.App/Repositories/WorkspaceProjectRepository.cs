using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Merge persistence of WorkspaceProjects by ProjectName: remove non-matching, add new, update existing.</summary>
public sealed partial class WorkspaceProjectRepository(
    AppDbContext dbContext,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    WorkspaceRepositoryCustomDependencyRepository customDependencyRepository,
    ILogger<WorkspaceProjectRepository> logger)
{
    /// <summary>Gets projects that have a PackageId (NuGet packages) for repositories linked to the given workspace.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.MatchedConnector)
            .Where(p => p.WorkspaceId == workspaceId && p.PackageId != null && p.PackageId != "")
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.TargetFramework)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Gets workspace packages whose PackageId (or ProjectName) is in the given set. Used to sync registries only for selected packages.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAndPackageIdsAsync(int workspaceId, IReadOnlySet<string> packageIds, CancellationToken cancellationToken = default)
    {
        if (packageIds.Count == 0) return new List<WorkspaceProject>();
        var all = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.MatchedConnector)
            .Where(p => p.WorkspaceId == workspaceId && (p.PackageId != null || p.ProjectName != null))
            .ToListAsync(cancellationToken);
        return all.Where(p =>
        {
            var id = (p.PackageId ?? p.ProjectName)?.Trim() ?? "";
            return !string.IsNullOrEmpty(id) && packageIds.Contains(id);
        }).ToList();
    }

    /// <summary>Updates MatchedConnectorId for workspace packages. Keys are ProjectId, values are ConnectorId (or null to clear).</summary>
    public async Task SetPackagesMatchedConnectorsAsync(int workspaceId, IReadOnlyDictionary<int, int?> projectIdToConnectorId, CancellationToken cancellationToken = default)
    {
        if (projectIdToConnectorId.Count == 0)
        {
            logger.LogTrace("SetPackagesMatchedConnectors: workspace {WorkspaceId}, no updates (empty map).", workspaceId);
            return;
        }
        var projectIds = projectIdToConnectorId.Keys.ToList();
        var projects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && projectIds.Contains(p.ProjectId))
            .ToListAsync(cancellationToken);
        logger.LogTrace("SetPackagesMatchedConnectors: workspace {WorkspaceId}, loaded {Loaded} projects for {Requested} requested.", workspaceId, projects.Count, projectIds.Count);
        foreach (var p in projects)
        {
            if (projectIdToConnectorId.TryGetValue(p.ProjectId, out var connectorId))
            {
                var previous = p.MatchedConnectorId;
                p.MatchedConnectorId = connectorId;
                logger.LogTrace("SetPackagesMatchedConnectors: ProjectId={ProjectId} MatchedConnectorId {Previous} -> {ConnectorId}.", p.ProjectId, previous ?? -1, connectorId ?? -1);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("SetPackagesMatchedConnectors: persisted {Count} package(s) for workspace {WorkspaceId}.", projects.Count, workspaceId);
    }

    /// <summary>Gets all projects for repositories linked to the given workspace.</summary>
    public async Task<List<WorkspaceProject>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.ProjectType == ProjectType.Service ? 0 : p.ProjectType == ProjectType.Library ? 1 : p.ProjectType == ProjectType.Package ? 2 : p.ProjectType == ProjectType.Test ? 3 : 4)
            .ThenBy(p => p.ProjectName)
            .ToListAsync(cancellationToken);
    }
}
