using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Merge persistence of WorkspaceProjects by ProjectName: remove non-matching, add new, update existing.</summary>
public sealed partial class WorkspaceProjectRepository(
    AppDbContext dbContext,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    WorkspaceRepositoryCustomDependencyRepository customDependencyRepository,
    ILogger<WorkspaceProjectRepository> logger,
    GrayMoon.Application.Repositories.Policy.IWorkspaceRepositoryOperationPolicy operationPolicy,
    GrayMoon.App.Services.Features.FeatureFinalizationCoordinator? finalizationCoordinator = null)
{
    /// <summary>Gets projects that have a PackageId (NuGet packages) for repositories linked to the given workspace, across every Feature context. Prefer the context-scoped overload for anything the user views while a Feature is selected.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.MatchedConnector)
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId && p.PackageId != null && p.PackageId != "")
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.TargetFramework)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Gets packages scoped to a single Feature context, same fallback rule as <see cref="GetByWorkspaceIdAsync(int, int, CancellationToken)"/>: a real package is included only for that context (or legacy null-context rows for the special Workspace), while generated/virtual package rows (<see cref="WorkspaceProject.IsGenerated"/>) are workspace-global and always included.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var isSpecialWorkspace = await IsSpecialWorkspaceContextAsync(workspaceFeatureContextId, cancellationToken);
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.MatchedConnector)
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId && p.PackageId != null && p.PackageId != "" && (
                p.IsGenerated
                || p.WorkspaceFeatureContextId == workspaceFeatureContextId
                || (isSpecialWorkspace && p.WorkspaceFeatureContextId == null)))
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.TargetFramework)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Gets workspace packages whose PackageId is in the given set (a project without a PackageId is never a package, whatever its name). Used to sync registries only for selected packages.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAndPackageIdsAsync(int workspaceId, IReadOnlySet<string> packageIds, CancellationToken cancellationToken = default)
    {
        if (packageIds.Count == 0) return new List<WorkspaceProject>();
        var all = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.MatchedConnector)
            .Where(p => p.WorkspaceId == workspaceId && p.PackageId != null && p.PackageId != "")
            .ToListAsync(cancellationToken);
        return all.Where(p =>
        {
            var id = p.PackageId?.Trim() ?? "";
            return !string.IsNullOrEmpty(id) && packageIds.Contains(id);
        }).ToList();
    }

    /// <summary>
    /// Updates MatchedConnectorId for workspace packages. Keys are ProjectId, values are ConnectorId (or null to clear).
    /// The ids come from an earlier read and span every context of the Workspace, and a project merge of any of
    /// those contexts may have removed a row since (a registry lookup takes seconds). So the rows are not loaded
    /// and saved: each connector is written with one set-based UPDATE, which skips a row that is gone instead of
    /// failing the whole save for the rows that are still there.
    /// </summary>
    public async Task SetPackagesMatchedConnectorsAsync(int workspaceId, IReadOnlyDictionary<int, int?> projectIdToConnectorId, CancellationToken cancellationToken = default)
    {
        if (projectIdToConnectorId.Count == 0)
        {
            logger.LogTrace("SetPackagesMatchedConnectors: workspace {WorkspaceId}, no updates (empty map).", workspaceId);
            return;
        }

        var projectIdsByConnector = projectIdToConnectorId
            .GroupBy(kv => kv.Value, kv => kv.Key)
            .Select(g => (ConnectorId: g.Key, ProjectIds: g.ToList()))
            .ToList();

        // Several statements are one change, as the single save was. A caller's own transaction is joined.
        var ownsTransaction = projectIdsByConnector.Count > 1 && dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var updated = 0;
        foreach (var (connectorId, projectIds) in projectIdsByConnector)
        {
            updated += await dbContext.WorkspaceProjects
                .Where(p => p.WorkspaceId == workspaceId && projectIds.Contains(p.ProjectId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.MatchedConnectorId, connectorId), cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        // The UPDATE goes past the change tracker. A row this context already tracks gets the stored value, so a
        // later tracking query on the same (circuit-long) context does not hand out the old one.
        foreach (var entry in dbContext.ChangeTracker.Entries<WorkspaceProject>().ToList())
        {
            if (entry.State is not (EntityState.Unchanged or EntityState.Modified)
                || entry.Entity.WorkspaceId != workspaceId
                || !projectIdToConnectorId.TryGetValue(entry.Entity.ProjectId, out var connectorId))
                continue;

            var property = entry.Property(p => p.MatchedConnectorId);
            property.CurrentValue = connectorId;
            property.OriginalValue = connectorId;
            property.IsModified = false;
        }

        logger.LogTrace(
            "SetPackagesMatchedConnectors: workspace {WorkspaceId}, updated {Updated} of {Requested} requested project(s); the others no longer exist.",
            workspaceId, updated, projectIdToConnectorId.Count);
    }

    /// <summary>Gets all projects for repositories linked to the given workspace, across every Feature context. Prefer the context-scoped overload for anything that computes or persists a dependency graph.</summary>
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

    /// <summary>Gets all projects for repositories linked to the given workspace, scoped to a single Feature context so a Feature's project graph never mixes with the Workspace's (or another Feature's) rows for the same repo/path. Generated/virtual package rows (<see cref="WorkspaceProject.IsGenerated"/>) are workspace-global (owned by the special Workspace context) so they are always included via <c>IsGenerated</c>. For the special Workspace context, legacy rows written before this project started stamping a context id (<see cref="WorkspaceProject.WorkspaceFeatureContextId"/> is null) are also included, so an upgraded database that has not resynced yet does not lose its own projects.</summary>
    public async Task<List<WorkspaceProject>> GetByWorkspaceIdAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var isSpecialWorkspace = await IsSpecialWorkspaceContextAsync(workspaceFeatureContextId, cancellationToken);
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId && (
                p.IsGenerated
                || p.WorkspaceFeatureContextId == workspaceFeatureContextId
                || (isSpecialWorkspace && p.WorkspaceFeatureContextId == null)))
            .OrderBy(p => p.ProjectType == ProjectType.Service ? 0 : p.ProjectType == ProjectType.Library ? 1 : p.ProjectType == ProjectType.Package ? 2 : p.ProjectType == ProjectType.Test ? 3 : 4)
            .ThenBy(p => p.ProjectName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Gets all projects for repositories linked to the given workspace, scoped by an optional <see cref="WorkspaceFeatureContextId"/>. Passing <c>null</c> reproduces the legacy, unscoped <see cref="GetByWorkspaceIdAsync(int, CancellationToken)"/> behavior exactly (rule 1 of feature-context-scoping.mdc); passing a value dispatches to the context-scoped overload above.</summary>
    public Task<List<WorkspaceProject>> GetByWorkspaceIdAsync(int workspaceId, WorkspaceFeatureContextId? contextId, CancellationToken cancellationToken = default) =>
        contextId is null
            ? GetByWorkspaceIdAsync(workspaceId, cancellationToken)
            : GetByWorkspaceIdAsync(workspaceId, contextId.Value.Value, cancellationToken);

    /// <summary>True when <paramref name="workspaceFeatureContextId"/> is the special Workspace context (<see cref="WorkspaceFeatureContextKind.Workspace"/>), false for a Feature context.</summary>
    private async Task<bool> IsSpecialWorkspaceContextAsync(int workspaceFeatureContextId, CancellationToken cancellationToken) =>
        await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);
}
