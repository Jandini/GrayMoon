using System.Collections.Concurrent;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>Syncs workspace packages to NuGet registries: for each package, finds which connector (registry) contains it and persists MatchedConnectorId. Matching is by package ID only (any version).</summary>
public sealed class PackageRegistrySyncService(
    ConnectorRepository connectorRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    NuGetService nuGetService,
    ILogger<PackageRegistrySyncService> logger)
{
    private const int MaxParallelPackageLookups = 8;

    /// <summary>For each package in the workspace, checks all active NuGet connectors in parallel and sets MatchedConnectorId to the first registry that contains the package (by ID; no particular version required). Up to 8 packages are checked in parallel.</summary>
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

        var projectIdToConnectorId = new ConcurrentDictionary<int, int?>();
        var total = packages.Count;
        var completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelPackageLookups,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(packages, options, async (p, ct) =>
        {
            var packageId = (p.PackageId ?? p.ProjectName).Trim();
            if (string.IsNullOrEmpty(packageId))
            {
                logger.LogTrace("Package ProjectId={ProjectId}: empty PackageId, skipping.", p.ProjectId);
                projectIdToConnectorId[p.ProjectId] = null;
                var c = Interlocked.Increment(ref completed);
                progress?.Report((c, total));
                return;
            }

            // Try all NuGet connectors in parallel; match by package ID only (no particular version required).
            int? matchedConnectorId = null;
            var lookupTasks = connectors.Select(async connector =>
            {
                try
                {
                    logger.LogTrace("Registry lookup: PackageId={PackageId}, trying connector {ConnectorName} (Id={ConnectorId}).", packageId, connector.ConnectorName, connector.ConnectorId);
                    var exists = await nuGetService.PackageExistsAsync(connector, packageId, ct);
                    if (exists)
                        logger.LogTrace("Registry match: PackageId={PackageId} found in connector {ConnectorName} (Id={ConnectorId}).", packageId, connector.ConnectorName, connector.ConnectorId);
                    else
                        logger.LogTrace("Registry lookup: PackageId={PackageId} not in connector {ConnectorName}.", packageId, connector.ConnectorName);
                    return (connector, exists);
                }
                catch (Exception ex)
                {
                    logger.LogTrace(ex, "Registry lookup: PackageId={PackageId} error for connector {ConnectorName}: {Message}.", packageId, connector.ConnectorName, ex.Message);
                    return (connector, false);
                }
            });
            var results = await Task.WhenAll(lookupTasks);
            var firstMatch = results.FirstOrDefault(r => r.Item2);
            if (firstMatch.Item1 != null)
                matchedConnectorId = firstMatch.Item1.ConnectorId;
            if (matchedConnectorId == null)
                logger.LogTrace("Registry lookup: PackageId={PackageId} matched no connector.", packageId);
            projectIdToConnectorId[p.ProjectId] = matchedConnectorId;
            var done = Interlocked.Increment(ref completed);
            progress?.Report((done, total));
        });

        logger.LogTrace("Persisting {Count} package registry matches for workspace {WorkspaceId}.", projectIdToConnectorId.Count, workspaceId);
        await workspaceProjectRepository.SetPackagesMatchedConnectorsAsync(workspaceId, new Dictionary<int, int?>(projectIdToConnectorId), cancellationToken);
        logger.LogTrace("Synced package registries for workspace {WorkspaceId}: {PackageCount} packages, {ConnectorCount} NuGet connectors.", workspaceId, packages.Count, connectors.Count);
    }
}
