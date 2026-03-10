using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

/// <summary>
/// Background task that runs on startup to validate connector tokens (decrypt + remote check)
/// and set Connector.IsHealthy / Connector.LastError accordingly.
/// Uses the same TestConnectionAsync flow as the Connectors page.
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<AppDbContext>();
            var serviceFactory = services.GetRequiredService<ConnectorServiceFactory>();

            var connectors = await dbContext.Connectors.ToListAsync(stoppingToken);
            if (connectors.Count == 0)
            {
                logger.LogInformation("TokenHealthBackgroundService: no connectors found.");
                return;
            }

            using var semaphore = new SemaphoreSlim(_maxParallel, _maxParallel);
            var tasks = connectors.Select(async connector =>
            {
                await semaphore.WaitAsync(stoppingToken);
                try
                {
                    if (stoppingToken.IsCancellationRequested)
                        return;

                    // Default assumptions
                    connector.IsHealthy = false;

                    if (!connector.IsActive)
                    {
                        connector.LastError = "Connector is inactive.";
                        return;
                    }

                    var requiresToken = ConnectorHelpers.RequiresToken(connector.ConnectorType, connector.ApiBaseUrl);
                    if (!requiresToken)
                    {
                        connector.IsHealthy = true;
                        connector.LastError = null;
                        return;
                    }

                    var plainToken = ConnectorHelpers.UnprotectToken(connector.UserToken);
                    if (string.IsNullOrWhiteSpace(plainToken))
                    {
                        connector.IsHealthy = false;
                        connector.LastError = "Token is required but missing or empty.";
                        return;
                    }

                    // Use the same connector-service pattern as the Connectors page.
                    // GitHubService/NuGetService will handle decrypted token usage.
                    var svc = serviceFactory.GetService(connector.ConnectorType);
                    var healthy = await svc.TestConnectionAsync(connector);

                    connector.IsHealthy = healthy;
                    connector.LastError = healthy ? null : "Remote service rejected the token. Update the connector token.";
                    if (healthy)
                    {
                        logger.LogDebug("Token health check succeeded for connector {ConnectorId} ({ConnectorName}).",
                            connector.ConnectorId, connector.ConnectorName);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    connector.IsHealthy = false;
                    connector.LastError = $"Token health check failed: {ex.Message}";
                    logger.LogWarning(ex, "Token health check failed for connector {ConnectorId} ({ConnectorName})",
                        connector.ConnectorId, connector.ConnectorName);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            await dbContext.SaveChangesAsync(stoppingToken);
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
}


