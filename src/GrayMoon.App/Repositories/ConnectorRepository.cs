using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class ConnectorRepository(AppDbContext dbContext, ILogger<ConnectorRepository> logger)
{
    public async Task<List<Connector>> GetAllAsync()
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .Where(connector => connector.RemovedAtUtc == null)
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<List<Connector>> GetActiveAsync()
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .Where(connector => connector.IsActive && connector.RemovedAtUtc == null)
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<Connector?> GetByIdAsync(int connectorId)
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.ConnectorId == connectorId && connector.RemovedAtUtc == null);
    }

    public async Task<Connector?> GetByNameAsync(string connectorName)
    {
        var normalized = connectorName.Trim().ToLowerInvariant();
        return await dbContext.Connectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.RemovedAtUtc == null && connector.ConnectorName.ToLower() == normalized);
    }

    public async Task<Connector> AddAsync(Connector connector)
    {
        if (await ConnectorNameExistsAsync(connector.ConnectorName))
        {
            throw new InvalidOperationException($"Connector name '{connector.ConnectorName}' already exists.");
        }

        connector.UserToken = ConnectorHelpers.ProtectToken(connector.UserToken);
        connector.Status = string.IsNullOrWhiteSpace(connector.Status) ? "Unknown" : connector.Status;
        connector.LastError = string.IsNullOrWhiteSpace(connector.LastError) ? null : connector.LastError;

        dbContext.Connectors.Add(connector);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=Add, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connector.ConnectorId, connector.ConnectorName);
        return connector;
    }

    public async Task<Connector> UpdateAsync(Connector connector)
    {
        // Detach any stale Repository entities from prior operations so connector update is never blocked.
        foreach (var entry in dbContext.ChangeTracker.Entries<Repository>().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        foreach (var entry in dbContext.ChangeTracker.Entries<WorkspaceRepositoryLink>().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var existing = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connector.ConnectorId && item.RemovedAtUtc == null);

        if (existing == null)
        {
            throw new InvalidOperationException("Connector not found.");
        }

        if (await ConnectorNameExistsAsync(connector.ConnectorName, connector.ConnectorId))
        {
            throw new InvalidOperationException($"Connector name '{connector.ConnectorName}' already exists.");
        }

        existing.ConnectorName = connector.ConnectorName;
        existing.ConnectorType = connector.ConnectorType;
        existing.ApiBaseUrl = connector.ApiBaseUrl;
        existing.UserName = connector.UserName;
        existing.UserToken = ConnectorHelpers.ProtectToken(connector.UserToken);
        existing.Status = string.IsNullOrWhiteSpace(connector.Status) ? "Unknown" : connector.Status;
        existing.IsActive = connector.IsActive;
        existing.LastError = string.IsNullOrWhiteSpace(connector.LastError) ? null : connector.LastError;

        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=Update, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", existing.ConnectorId, existing.ConnectorName);
        return existing;
    }

    public async Task DeleteAsync(int connectorId)
    {
        // Detach any stale Repository/WorkspaceRepositoryLink entities that may have been dirtied by a
        // failed MergeRepositoriesAsync in this circuit-scope DbContext so token removal is never blocked.
        foreach (var entry in dbContext.ChangeTracker.Entries<Repository>().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        foreach (var entry in dbContext.ChangeTracker.Entries<WorkspaceRepositoryLink>().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorId} not found for deletion.", connectorId);
            return;
        }

        if (connector.RemovedAtUtc != null)
            return;

        // Connector state never removes a Workspace's repository. Repositories no Workspace links go with the
        // connector; a linked one stays with its links, branches, pull request rows and Feature rows.
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var unlinkedRepositoryIds = await dbContext.Repositories
            .Where(repository => repository.ConnectorId == connectorId)
            .Where(repository => !dbContext.WorkspaceRepositories.Any(link => link.RepositoryId == repository.RepositoryId))
            .Select(repository => repository.RepositoryId)
            .ToListAsync();
        if (unlinkedRepositoryIds.Count > 0)
        {
            await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(dbContext, [], unlinkedRepositoryIds);
            await dbContext.Repositories
                .Where(repository => unlinkedRepositoryIds.Contains(repository.RepositoryId))
                .ExecuteDeleteAsync();
        }

        var keptRepositoryCount = await dbContext.Repositories.CountAsync(repository => repository.ConnectorId == connectorId);
        var connectorName = connector.ConnectorName;
        if (keptRepositoryCount == 0)
        {
            dbContext.Connectors.Remove(connector);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            logger.LogInformation("Persistence: saved Connector. Action=Delete, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connectorId, connectorName);
            return;
        }

        // Soft delete: the row stays only so the kept repositories still have a connector row. It loses its token,
        // is hidden from every list, and gives its name back (the name column is unique) so the same name can be
        // added again. RemovedConnectorCleanup purges it once its last repository moved to another connector.
        connector.RemovedAtUtc = DateTime.UtcNow;
        connector.UserToken = null;
        connector.IsActive = false;
        connector.IsHealthy = false;
        connector.Status = "Removed";
        connector.LastError = null;
        connector.ConnectorName = RemovedName(connectorName, connectorId);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        logger.LogInformation(
            "Persistence: saved Connector. Action=SoftDelete, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}, KeptWorkspaceRepositories={KeptRepositoryCount}",
            connectorId, connectorName, keptRepositoryCount);
    }

    /// <summary>How many of the connector's repositories a Workspace uses. Removing the connector keeps these without a valid connector.</summary>
    public async Task<int> CountWorkspaceRepositoriesAsync(int connectorId)
    {
        return await dbContext.Repositories
            .Where(repository => repository.ConnectorId == connectorId)
            .CountAsync(repository => dbContext.WorkspaceRepositories.Any(link => link.RepositoryId == repository.RepositoryId));
    }

    private static string RemovedName(string connectorName, int connectorId)
    {
        var suffix = $" (removed {connectorId})";
        var maxNameLength = 100 - suffix.Length;
        return (connectorName.Length > maxNameLength ? connectorName[..maxNameLength] : connectorName) + suffix;
    }

    public async Task UpdateStatusAsync(int connectorId, string status, string? errorMessage)
    {
        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId && item.RemovedAtUtc == null);

        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorId} not found for status update.", connectorId);
            return;
        }

        connector.Status = status;
        connector.LastError = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
        connector.IsHealthy = status == "Ok";
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=UpdateStatus, ConnectorId={ConnectorId}, Status={Status}", connectorId, status);
    }

    public async Task UpdateIsActiveAsync(int connectorId, bool isActive)
    {
        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId && item.RemovedAtUtc == null);

        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorId} not found for active toggle.", connectorId);
            return;
        }

        connector.IsActive = isActive;
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=UpdateIsActive, ConnectorId={ConnectorId}, IsActive={IsActive}", connectorId, isActive);
    }

    private async Task<bool> ConnectorNameExistsAsync(string connectorName, int? ignoreId = null)
    {
        var normalized = connectorName.Trim().ToLowerInvariant();
        return await dbContext.Connectors
            .AnyAsync(connector =>
                connector.ConnectorId != ignoreId &&
                connector.ConnectorName.ToLower() == normalized);
    }
}
