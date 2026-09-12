using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Connectors;

/// <summary>Helper/service for connector health: determines whether tokens are used, and enforces health before agent operations.</summary>
public sealed class ConnectorHealthService(AppDbContext dbContext, ILogger<ConnectorHealthService> logger)
{
    /// <summary>
    /// Returns true when there is at least one connector that:
    /// - is active,
    /// - is not healthy,
    /// - and is actually used by some repository in some workspace.
    /// </summary>
    public async Task<bool> AnyUsedConnectorUnhealthyAsync(CancellationToken cancellationToken = default)
    {
        var query =
            from wr in dbContext.WorkspaceRepositories
            join repo in dbContext.Repositories on wr.RepositoryId equals repo.RepositoryId
            join connector in dbContext.Connectors on repo.ConnectorId equals connector.ConnectorId
            where connector.IsActive && !connector.IsHealthy
            select connector.ConnectorId;

        var any = await query.Distinct().AnyAsync(cancellationToken);
        if (any)
        {
            logger.LogDebug("Detected at least one unhealthy connector that is used by a workspace repository.");
        }
        else
        {
            logger.LogDebug("All connectors used by workspace repositories are currently healthy.");
        }

        return any;
    }

    /// <summary>
    /// Ensures that the connector associated with the given repository is healthy when a token is required.
    /// Throws InvalidOperationException with a user-friendly message if the token is not healthy.
    /// </summary>
    public async Task EnsureConnectorHealthyForRepositoryAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        var repo = await dbContext.Repositories
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);

        if (repo?.Connector == null)
        {
            // No connector; nothing to check.
            return;
        }

        EnsureHealthy(repo.Connector);
    }

    /// <summary>
    /// Batched form of <see cref="EnsureConnectorHealthyForRepositoryAsync"/>: checks every repository in
    /// <paramref name="repositoryIds"/> using one set-based query against a single DbContext instance instead of
    /// one round-trip per repository. Throws for the first unhealthy connector encountered, in the same order as
    /// <paramref name="repositoryIds"/> (matching the sequential-loop behavior this replaces). Repository ids that
    /// no longer exist, or that have no connector, are silently skipped - same as the single-repository overload.
    /// </summary>
    public async Task EnsureConnectorsHealthyForRepositoriesAsync(IEnumerable<int> repositoryIds, CancellationToken cancellationToken = default)
    {
        var ids = repositoryIds as IReadOnlyCollection<int> ?? repositoryIds.ToList();
        if (ids.Count == 0)
            return;

        var repos = await dbContext.Repositories
            .Include(r => r.Connector)
            .Where(r => ids.Contains(r.RepositoryId))
            .ToListAsync(cancellationToken);

        var reposById = repos.ToDictionary(r => r.RepositoryId);

        foreach (var repositoryId in ids)
        {
            if (!reposById.TryGetValue(repositoryId, out var repo) || repo.Connector == null)
                continue;

            EnsureHealthy(repo.Connector);
        }
    }

    /// <summary>Shared health check applied to a single connector; throws ConnectorHealthException when unhealthy.</summary>
    private static void EnsureHealthy(Connector connector)
    {
        var requiresToken = ConnectorHelpers.RequiresToken(connector.ConnectorType, connector.ApiBaseUrl);

        if (!requiresToken)
            return;

        if (!connector.IsActive)
        {
            var msg = $"Connector '{connector.ConnectorName}' is inactive. Activate or update it on the Connectors page.";
            throw new ConnectorHealthException(msg);
        }

        if (connector.IsHealthy)
            return;

        var error = string.IsNullOrWhiteSpace(connector.LastError)
            ? $"Connector '{connector.ConnectorName}' token is not healthy. Update it on the Connectors page."
            : connector.LastError;

        throw new ConnectorHealthException(error);
    }
}

