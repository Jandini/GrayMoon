using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>Syncs workspace packages to NuGet registries: for each package, finds which connector (registry) contains it and persists MatchedConnectorId.</summary>
public sealed class PackageRegistrySyncService(
    ConnectorRepository connectorRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    NuGetService nuGetService,
    ILogger<PackageRegistrySyncService> logger)
{
    /// <summary>For each package in the workspace, checks active NuGet connectors and sets MatchedConnectorId to the first registry that contains the package.</summary>
    public async Task SyncWorkspacePackageRegistriesAsync(
        int workspaceId,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packages = await workspaceProjectRepository.GetPackagesByWorkspaceIdAsync(workspaceId, cancellationToken);
        logger.LogTrace("Sync registries workspace {WorkspaceId}: loaded {PackageCount} packages.", workspaceId, packages.Count);
        if (packages.Count == 0)
        {
            progress?.Report((0, 0));
            return;
        }

        var connectors = (await connectorRepository.GetActiveAsync())
            .Where(c => c.ConnectorType == ConnectorType.NuGet)
            .ToList();
        logger.LogTrace("Sync registries workspace {WorkspaceId}: {ConnectorCount} active NuGet connectors.", workspaceId, connectors.Count);
        if (connectors.Count == 0)
        {
            logger.LogTrace("No active NuGet connectors; clearing registry match for workspace {WorkspaceId}.", workspaceId);
            var clear = packages.ToDictionary(p => p.ProjectId, _ => (int?)null);
            await workspaceProjectRepository.SetPackagesMatchedConnectorsAsync(workspaceId, clear, cancellationToken);
            progress?.Report((packages.Count, packages.Count));
            return;
        }

        var projectIdToConnectorId = new Dictionary<int, int?>();
        var total = packages.Count;
        var completed = 0;

        foreach (var p in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageId = (p.PackageId ?? p.ProjectName).Trim();
            if (string.IsNullOrEmpty(packageId))
            {
                logger.LogTrace("Package ProjectId={ProjectId}: empty PackageId, skipping.", p.ProjectId);
                projectIdToConnectorId[p.ProjectId] = null;
                completed++;
                progress?.Report((completed, total));
                continue;
            }

            int? matchedConnectorId = null;
            foreach (var connector in connectors)
            {
                try
                {
                    logger.LogTrace("Registry lookup: PackageId={PackageId}, trying connector {ConnectorName} (Id={ConnectorId}).", packageId, connector.ConnectorName, connector.ConnectorId);
                    if (await nuGetService.PackageExistsAsync(connector, packageId, cancellationToken))
                    {
                        logger.LogTrace("Registry match: PackageId={PackageId} found in connector {ConnectorName} (Id={ConnectorId}).", packageId, connector.ConnectorName, connector.ConnectorId);
                        matchedConnectorId = connector.ConnectorId;
                        break;
                    }
                    logger.LogTrace("Registry lookup: PackageId={PackageId} not in connector {ConnectorName}.", packageId, connector.ConnectorName);
                }
                catch (Exception ex)
                {
                    logger.LogTrace(ex, "Registry lookup: PackageId={PackageId} error for connector {ConnectorName}: {Message}.", packageId, connector.ConnectorName, ex.Message);
                }
            }

            if (matchedConnectorId == null)
                logger.LogTrace("Registry lookup: PackageId={PackageId} matched no connector.", packageId);
            projectIdToConnectorId[p.ProjectId] = matchedConnectorId;
            completed++;
            progress?.Report((completed, total));
        }

        logger.LogTrace("Persisting {Count} package registry matches for workspace {WorkspaceId}.", projectIdToConnectorId.Count, workspaceId);
        await workspaceProjectRepository.SetPackagesMatchedConnectorsAsync(workspaceId, projectIdToConnectorId, cancellationToken);
        logger.LogTrace("Synced package registries for workspace {WorkspaceId}: {PackageCount} packages, {ConnectorCount} NuGet connectors.", workspaceId, packages.Count, connectors.Count);
    }
}
