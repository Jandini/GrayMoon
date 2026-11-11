using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>
/// A removed connector (<see cref="Models.Connector.RemovedAtUtc"/>) stays only while a Workspace still uses one
/// of its repositories. This purges what is no longer needed: its repositories no Workspace links, and then the
/// connector row itself once it has no repository left (they moved to another connector, or left their Workspaces).
/// </summary>
internal static class RemovedConnectorCleanup
{
    /// <summary>Returns the number of removed connectors that were purged. Runs on the caller's transaction, if any.</summary>
    public static async Task<int> PurgeUnusedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var removedConnectorIds = await db.Connectors
            .Where(c => c.RemovedAtUtc != null)
            .Select(c => c.ConnectorId)
            .ToListAsync(cancellationToken);
        if (removedConnectorIds.Count == 0)
            return 0;

        var unlinkedRepositoryIds = await db.Repositories
            .Where(r => removedConnectorIds.Contains(r.ConnectorId))
            .Where(r => !db.WorkspaceRepositories.Any(l => l.RepositoryId == r.RepositoryId))
            .Select(r => r.RepositoryId)
            .ToListAsync(cancellationToken);
        if (unlinkedRepositoryIds.Count > 0)
        {
            await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(db, [], unlinkedRepositoryIds, cancellationToken: cancellationToken);
            await db.Repositories
                .Where(r => unlinkedRepositoryIds.Contains(r.RepositoryId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return await db.Connectors
            .Where(c => c.RemovedAtUtc != null)
            .Where(c => !db.Repositories.Any(r => r.ConnectorId == c.ConnectorId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
