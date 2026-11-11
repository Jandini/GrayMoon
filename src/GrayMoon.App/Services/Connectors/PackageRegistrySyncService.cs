using System.Collections.Concurrent;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services.Connectors;

/// <summary>Syncs workspace packages to NuGet registries: for each package, finds which connector (registry) contains it and persists MatchedConnectorId. Matching is by package ID only (any version).</summary>
public sealed class PackageRegistrySyncService(
    ConnectorRepository connectorRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    NuGetService nuGetService,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<PackageRegistrySyncService> logger)
{
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

        var maxParallel = Math.Max(1, workspaceOptions.Value.MaxParallelOperations);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel,
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
            var (matchedConnectorId, lookupFailed) = await FindRegistryAsync(connectors, packageId, ct);
            // A failed lookup is not "no registry has this package": the stored match is left as it is, so a
            // network error never clears a known mapping and the next sync retries.
            if (matchedConnectorId != null || !lookupFailed)
                projectIdToConnectorId[p.ProjectId] = matchedConnectorId;
            var done = Interlocked.Increment(ref completed);
            progress?.Report((done, total));
        });

        logger.LogTrace("Persisting {Count} package registry matches for workspace {WorkspaceId}.", projectIdToConnectorId.Count, workspaceId);
        await workspaceProjectRepository.SetPackagesMatchedConnectorsAsync(workspaceId, new Dictionary<int, int?>(projectIdToConnectorId), cancellationToken);
        logger.LogTrace("Synced package registries for workspace {WorkspaceId}: {PackageCount} packages, {ConnectorCount} NuGet connectors.", workspaceId, packages.Count, connectors.Count);
    }

    /// <summary>
    /// Syncs registry matches only for workspace packages whose PackageId is in <paramref name="packageIds"/>.
    /// A package created or first published by an earlier level of the current run has no registry until that
    /// level has published it, so synchronized push calls this again for ids that are still unmatched.
    /// Projects that already have <see cref="WorkspaceProject.MatchedConnectorId"/> are left alone: a miss is
    /// stored as null and would clear a match that is already known.
    /// </summary>
    public async Task SyncRegistriesForPackageIdsAsync(
        int workspaceId,
        IReadOnlySet<string> packageIds,
        CancellationToken cancellationToken = default)
    {
        if (packageIds == null || packageIds.Count == 0) return;
        var packages = await workspaceProjectRepository.GetPackagesByWorkspaceIdAndPackageIdsAsync(workspaceId, packageIds, cancellationToken);
        logger.LogTrace("Sync registries for {PackageCount} selected packages in workspace {WorkspaceId}.", packages.Count, workspaceId);
        if (packages.Count == 0) return;

        var connectors = (await connectorRepository.GetActiveAsync())
            .Where(c => c.ConnectorType == ConnectorType.NuGet)
            .ToList();
        if (connectors.Count == 0) return;

        var projectIdToConnectorId = new ConcurrentDictionary<int, int?>();
        var total = packages.Count;
        var completed = 0;
        var maxParallel = Math.Max(1, workspaceOptions.Value.MaxParallelOperations);
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(packages, options, async (p, ct) =>
        {
            if (p.MatchedConnectorId.HasValue)
            {
                Interlocked.Increment(ref completed);
                return;
            }

            var packageId = (p.PackageId ?? p.ProjectName).Trim();
            if (string.IsNullOrEmpty(packageId))
            {
                projectIdToConnectorId[p.ProjectId] = null;
                Interlocked.Increment(ref completed);
                return;
            }
            var (matchedConnectorId, lookupFailed) = await FindRegistryAsync(connectors, packageId, ct);
            // A failed lookup is not "no registry has this package": the stored match is left as it is, so a
            // network error never clears a known mapping and the next sync retries.
            if (matchedConnectorId != null || !lookupFailed)
                projectIdToConnectorId[p.ProjectId] = matchedConnectorId;
            Interlocked.Increment(ref completed);
        });

        await workspaceProjectRepository.SetPackagesMatchedConnectorsAsync(workspaceId, new Dictionary<int, int?>(projectIdToConnectorId), cancellationToken);
    }

    /// <summary>
    /// Asks every NuGet connector whether it has the package (by id, any version). A lookup that throws is logged
    /// and reported as failed, never as "does not exist".
    /// </summary>
    private async Task<(int? MatchedConnectorId, bool LookupFailed)> FindRegistryAsync(
        IReadOnlyList<Connector> connectors, string packageId, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(connectors.Select(async connector =>
        {
            try
            {
                var exists = await nuGetService.PackageExistsAsync(connector, packageId, cancellationToken);
                logger.LogTrace("Registry lookup: PackageId={PackageId}, connector {ConnectorName}: {Result}.", packageId, connector.ConnectorName, exists ? "found" : "not found");
                return (Connector: connector, Exists: exists, Failed: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Registry lookup failed for package {PackageId} on connector {ConnectorName}. The package's registry match is left unchanged.", packageId, connector.ConnectorName);
                return (Connector: connector, Exists: false, Failed: true);
            }
        }));

        var match = results.FirstOrDefault(r => r.Exists);
        return (match.Connector?.ConnectorId, results.Any(r => r.Failed));
    }
}
