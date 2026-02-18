using GrayMoon.App.Api;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Api.Endpoints;

public static class BranchEndpoints
{
    public static IEndpointRouteBuilder MapBranchEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/branches/get", GetBranches);
        routes.MapPost("/api/branches/checkout", CheckoutBranch);
        routes.MapPost("/api/branches/sync-to-default", SyncToDefaultBranch);
        return routes;
    }

    private static async Task<IResult> GetBranches(
        GetBranchesApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository repoRepository,
        AppDbContext dbContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        var repositoryId = body.RepositoryId;

        if (workspaceId <= 0 || repositoryId <= 0)
            return Results.BadRequest("workspaceId and repositoryId are required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found.");

        var repo = await repoRepository.GetByIdAsync(repositoryId);
        if (repo == null)
            return Results.NotFound("Repository not found.");

        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (!isInWorkspace)
            return Results.NotFound("Repository is not in the given workspace.");

        if (!agentBridge.IsAgentConnected)
            return Results.Problem("Agent not connected.", statusCode: 503);

        try
        {
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName
            };
            var response = await agentBridge.SendCommandAsync("GetBranches", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to get branches", statusCode: 500);

            return Results.Ok(response.Data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting branches for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while getting branches", statusCode: 500);
        }
    }

    private static async Task<IResult> CheckoutBranch(
        CheckoutBranchApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository repoRepository,
        AppDbContext dbContext,
        IHubContext<WorkspaceSyncHub> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        var repositoryId = body.RepositoryId;
        var branchName = body.BranchName;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found.");

        var repo = await repoRepository.GetByIdAsync(repositoryId);
        if (repo == null)
            return Results.NotFound("Repository not found.");

        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (!isInWorkspace)
            return Results.NotFound("Repository is not in the given workspace.");

        if (!agentBridge.IsAgentConnected)
            return Results.Problem("Agent not connected.", statusCode: 503);

        try
        {
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName
            };
            var response = await agentBridge.SendCommandAsync("CheckoutBranch", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to checkout branch", statusCode: 500);

            // Broadcast update to refresh UI
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(response.Data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking out branch for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while checking out branch", statusCode: 500);
        }
    }

    private static async Task<IResult> SyncToDefaultBranch(
        SyncToDefaultBranchApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository repoRepository,
        AppDbContext dbContext,
        IHubContext<WorkspaceSyncHub> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        var repositoryId = body.RepositoryId;
        var currentBranchName = body.CurrentBranchName;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(currentBranchName))
            return Results.BadRequest("workspaceId, repositoryId, and currentBranchName are required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found.");

        var repo = await repoRepository.GetByIdAsync(repositoryId);
        if (repo == null)
            return Results.NotFound("Repository not found.");

        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (!isInWorkspace)
            return Results.NotFound("Repository is not in the given workspace.");

        if (!agentBridge.IsAgentConnected)
            return Results.Problem("Agent not connected.", statusCode: 503);

        try
        {
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                currentBranchName
            };
            var response = await agentBridge.SendCommandAsync("SyncToDefaultBranch", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to sync to default branch", statusCode: 500);

            // Broadcast update to refresh UI
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(response.Data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while syncing to default branch", statusCode: 500);
        }
    }
}

public sealed class GetBranchesApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
}

public sealed class CheckoutBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
}

public sealed class SyncToDefaultBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? CurrentBranchName { get; set; }
}
