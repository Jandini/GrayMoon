using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class ConnectorRepository(AppDbContext dbContext, ILogger<ConnectorRepository> logger)
{
    public async Task<List<Connector>> GetAllAsync()
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<List<Connector>> GetActiveAsync()
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .Where(connector => connector.IsActive)
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<Connector?> GetByIdAsync(int connectorId)
    {
        return await dbContext.Connectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.ConnectorId == connectorId);
    }

    public async Task<Connector?> GetByNameAsync(string connectorName)
    {
        var normalized = connectorName.Trim().ToLowerInvariant();
        return await dbContext.Connectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.ConnectorName.ToLower() == normalized);
    }

    public async Task<Connector> AddAsync(Connector connector)
    {
        if (await ConnectorNameExistsAsync(connector.ConnectorName))
        {
            throw new InvalidOperationException($"Connector name '{connector.ConnectorName}' already exists.");
        }

        connector.Status = string.IsNullOrWhiteSpace(connector.Status) ? "Unknown" : connector.Status;
        connector.LastError = string.IsNullOrWhiteSpace(connector.LastError) ? null : connector.LastError;

        dbContext.Connectors.Add(connector);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=Add, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connector.ConnectorId, connector.ConnectorName);
        return connector;
    }

    public async Task<Connector> UpdateAsync(Connector connector)
    {
        var existing = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connector.ConnectorId);

        if (existing == null)
        {
            throw new InvalidOperationException("Connector not found.");
        }

        if (await ConnectorNameExistsAsync(connector.ConnectorName, connector.ConnectorId))
        {
            throw new InvalidOperationException($"Connector name '{connector.ConnectorName}' already exists.");
        }

        existing.ConnectorName = connector.ConnectorName;
        existing.ApiBaseUrl = connector.ApiBaseUrl;
        existing.UserName = connector.UserName;
        existing.UserToken = connector.UserToken;
        existing.Status = string.IsNullOrWhiteSpace(connector.Status) ? "Unknown" : connector.Status;
        existing.IsActive = connector.IsActive;
        existing.LastError = string.IsNullOrWhiteSpace(connector.LastError) ? null : connector.LastError;

        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=Update, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", existing.ConnectorId, existing.ConnectorName);
        return existing;
    }

    public async Task DeleteAsync(int connectorId)
    {
        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorId} not found for deletion.", connectorId);
            return;
        }

        dbContext.Connectors.Remove(connector);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=Delete, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connectorId, connector.ConnectorName);
    }

    public async Task UpdateStatusAsync(int connectorId, string status, string? errorMessage)
    {
        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorId} not found for status update.", connectorId);
            return;
        }

        connector.Status = status;
        connector.LastError = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved Connector. Action=UpdateStatus, ConnectorId={ConnectorId}, Status={Status}", connectorId, status);
    }

    public async Task UpdateIsActiveAsync(int connectorId, bool isActive)
    {
        var connector = await dbContext.Connectors
            .FirstOrDefaultAsync(item => item.ConnectorId == connectorId);

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
