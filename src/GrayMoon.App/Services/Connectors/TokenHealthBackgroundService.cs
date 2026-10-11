using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Connectors;

/// <summary>
/// Background task that runs on startup to validate connector tokens (decrypt + remote check)
/// and set Connector.IsHealthy / Connector.LastError accordingly.
/// Uses the same TestConnectionAsync flow as the Connectors page.
/// HTTP checks run in parallel. Each check uses its own scope. Results are written on one thread.
/// </summary>
public sealed class TokenHealthBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TokenHealthBackgroundService> logger,
    IOptions<WorkspaceOptions> workspaceOptions) : BackgroundService
{
    private readonly int _maxParallel = Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TokenHealthBackgroundService starting token health check with max parallelism {MaxParallel}.", _maxParallel);

        try
        {
            List<int> connectorIds;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                var dbContext = listScope.ServiceProvider.GetRequiredService<AppDbContext>();
                connectorIds = await dbContext.Connectors
                    .AsNoTracking()
                    .Select(c => c.ConnectorId)
                    .ToListAsync(stoppingToken);
            }

            if (connectorIds.Count == 0)
            {
                logger.LogInformation("TokenHealthBackgroundService: no connectors found.");
                return;
            }

            using var semaphore = new SemaphoreSlim(_maxParallel, _maxParallel);
            var tasks = connectorIds.Select(id => CheckOneAsync(id, semaphore, stoppingToken));
            var results = await Task.WhenAll(tasks);

            await using var persistScope = scopeFactory.CreateAsyncScope();
            var persistDb = persistScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connectors = await persistDb.Connectors
                .Where(c => connectorIds.Contains(c.ConnectorId))
                .ToListAsync(stoppingToken);
            var byId = connectors.ToDictionary(c => c.ConnectorId);
            foreach (var result in results)
            {
                if (!byId.TryGetValue(result.ConnectorId, out var connector))
                    continue;
                connector.IsHealthy = result.IsHealthy;
                connector.LastError = result.LastError;
            }

            await persistDb.SaveChangesAsync(stoppingToken);
            logger.LogInformation("TokenHealthBackgroundService completed token health check for {Count} connector(s).",
                connectors.Count);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TokenHealthBackgroundService cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TokenHealthBackgroundService encountered an error while checking tokens.");
        }
    }

    private async Task<ConnectorHealthResult> CheckOneAsync(int connectorId, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        await semaphore.WaitAsync(stoppingToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connector = await dbContext.Connectors
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ConnectorId == connectorId, stoppingToken);
            if (connector == null)
                return new ConnectorHealthResult(connectorId, false, "Connector was removed.");

            if (!connector.IsActive)
                return new ConnectorHealthResult(connectorId, false, "Connector is inactive.");

            var requiresToken = ConnectorHelpers.RequiresToken(connector.ConnectorType, connector.ApiBaseUrl);
            if (!requiresToken)
                return new ConnectorHealthResult(connectorId, true, null);

            string? plainToken;
            try
            {
                plainToken = ConnectorHelpers.UnprotectToken(connector.UserToken);
            }
            catch (Exception)
            {
                // Tampered ciphertext, or a key that can no longer decrypt it (F1 R-F1a #6):
                // report it through the normal health UI instead of throwing.
                return new ConnectorHealthResult(connectorId, false, "Token needs to be re-entered.");
            }

            if (string.IsNullOrWhiteSpace(plainToken))
                return new ConnectorHealthResult(connectorId, false, "Token is required but missing or empty.");

            var serviceFactory = scope.ServiceProvider.GetRequiredService<ConnectorServiceFactory>();
            var svc = serviceFactory.GetService(connector.ConnectorType);
            var result = await svc.TestConnectionAsync(connector);
            if (result.Success)
            {
                logger.LogDebug("Token health check succeeded for connector {ConnectorId} ({ConnectorName}).",
                    connector.ConnectorId, connector.ConnectorName);
            }

            return new ConnectorHealthResult(
                connectorId,
                result.Success,
                result.Success ? null : (result.ErrorMessage ?? "Remote service rejected the token. Update the connector token."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token health check failed for connector {ConnectorId}", connectorId);
            return new ConnectorHealthResult(connectorId, false, $"Token health check failed: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private readonly record struct ConnectorHealthResult(int ConnectorId, bool IsHealthy, string? LastError);
}
