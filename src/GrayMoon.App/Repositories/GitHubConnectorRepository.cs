using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class GitHubConnectorRepository(AppDbContext dbContext, ILogger<GitHubConnectorRepository> logger)
{
    public async Task<List<GitHubConnector>> GetAllAsync()
    {
        return await dbContext.GitHubConnectors
            .AsNoTracking()
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<List<GitHubConnector>> GetActiveAsync()
    {
        return await dbContext.GitHubConnectors
            .AsNoTracking()
            .Where(connector => connector.IsActive)
            .OrderBy(connector => connector.ConnectorName)
            .ToListAsync();
    }

    public async Task<GitHubConnector?> GetByIdAsync(int connectorId)
    {
        return await dbContext.GitHubConnectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.GitHubConnectorId == connectorId);
    }

    public async Task<GitHubConnector?> GetByNameAsync(string connectorName)
    {
        var normalized = connectorName.Trim().ToLowerInvariant();
        return await dbContext.GitHubConnectors
            .AsNoTracking()
            .FirstOrDefaultAsync(connector => connector.ConnectorName.ToLower() == normalized);
    }

    public async Task<GitHubConnector> AddAsync(GitHubConnector connector)
    {
        if (await ConnectorNameExistsAsync(connector.ConnectorName))
        {
            throw new InvalidOperationException($"Connector name '{connector.ConnectorName}' already exists.");
        }

        connector.Status = string.IsNullOrWhiteSpace(connector.Status) ? "Unknown" : connector.Status;
        connector.LastError = string.IsNullOrWhiteSpace(connector.LastError) ? null : connector.LastError;

        dbContext.GitHubConnectors.Add(connector);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved GitHubConnector. Action=Add, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connector.GitHubConnectorId, connector.ConnectorName);
        return connector;
    }

    public async Task<GitHubConnector> UpdateAsync(GitHubConnector connector)
    {
        var existing = await dbContext.GitHubConnectors
            .FirstOrDefaultAsync(item => item.GitHubConnectorId == connector.GitHubConnectorId);

        if (existing == null)
        {
            throw new InvalidOperationException("Connector not found.");
        }

        if (await ConnectorNameExistsAsync(connector.ConnectorName, connector.GitHubConnectorId))
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
        logger.LogInformation("Persistence: saved GitHubConnector. Action=Update, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", existing.GitHubConnectorId, existing.ConnectorName);
        return existing;
    }

    public async Task DeleteAsync(int connectorId)
    {
        var connector = await dbContext.GitHubConnectors
            .FirstOrDefaultAsync(item => item.GitHubConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("GitHub connector {ConnectorId} not found for deletion.", connectorId);
            return;
        }

        dbContext.GitHubConnectors.Remove(connector);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved GitHubConnector. Action=Delete, ConnectorId={ConnectorId}, ConnectorName={ConnectorName}", connectorId, connector.ConnectorName);
    }

    public async Task UpdateStatusAsync(int connectorId, string status, string? errorMessage)
    {
        var connector = await dbContext.GitHubConnectors
            .FirstOrDefaultAsync(item => item.GitHubConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("GitHub connector {ConnectorId} not found for status update.", connectorId);
            return;
        }

        connector.Status = status;
        connector.LastError = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved GitHubConnector. Action=UpdateStatus, ConnectorId={ConnectorId}, Status={Status}", connectorId, status);
    }

    public async Task UpdateIsActiveAsync(int connectorId, bool isActive)
    {
        var connector = await dbContext.GitHubConnectors
            .FirstOrDefaultAsync(item => item.GitHubConnectorId == connectorId);

        if (connector == null)
        {
            logger.LogWarning("GitHub connector {ConnectorId} not found for active toggle.", connectorId);
            return;
        }

        connector.IsActive = isActive;
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persistence: saved GitHubConnector. Action=UpdateIsActive, ConnectorId={ConnectorId}, IsActive={IsActive}", connectorId, isActive);
    }

    private async Task<bool> ConnectorNameExistsAsync(string connectorName, int? ignoreId = null)
    {
        var normalized = connectorName.Trim().ToLowerInvariant();
        return await dbContext.GitHubConnectors
            .AnyAsync(connector =>
                connector.GitHubConnectorId != ignoreId &&
                connector.ConnectorName.ToLower() == normalized);
    }
}
