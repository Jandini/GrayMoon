using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Api.Endpoints;

public static class ConnectorEndpoints
{
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder routes)
    {
        // This endpoint is primarily for the Agent to obtain a connector-scoped token
        // for a given repository. It intentionally has no /api prefix to match the
        // planned shape: GET /repos/{repoId}/connector.
        routes.MapGet("/repos/{repoId:int}/connector", GetConnectorForRepository);
        return routes;
    }

    private static async Task<IResult> GetConnectorForRepository(
        int repoId,
        AppDbContext dbContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Connectors");

        if (repoId <= 0)
            return Results.BadRequest("repoId must be greater than 0.");

        var repo = await dbContext.Repositories
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repoId);

        if (repo == null)
            return Results.NotFound("Repository not found.");

        if (repo.Connector == null)
        {
            logger.LogWarning("GetConnectorForRepository: repository {RepositoryId} has no connector.", repoId);
            return Results.Problem("Repository has no connector configured.", statusCode: 409);
        }

        var token = ConnectorHelpers.UnprotectToken(repo.Connector.UserToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("GetConnectorForRepository: connector {ConnectorId} for repository {RepositoryId} has no usable token.", repo.Connector.ConnectorId, repoId);
            return Results.Problem("Connector has no token configured.", statusCode: 409);
        }

        return Results.Ok(new
        {
            connectorId = repo.Connector.ConnectorId,
            token
        });
    }
}

