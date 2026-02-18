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
        routes.MapPost("/api/branches/refresh", RefreshBranches);
        routes.MapPost("/api/branches/checkout", CheckoutBranch);
        routes.MapPost("/api/branches/sync-to-default", SyncToDefaultBranch);
        return routes;
    }

    private static async Task<IResult> GetBranches(
        GetBranchesApiRequest? body,
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

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (wr == null)
            return Results.NotFound("Repository is not in the given workspace.");

        try
        {
            // Read branches from database
            var branches = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .ToListAsync();

            var localBranches = branches
                .Where(b => !b.IsRemote)
                .Select(b => b.BranchName)
                .OrderBy(b => b)
                .ToList();

            var remoteBranches = branches
                .Where(b => b.IsRemote)
                .Select(b => b.BranchName)
                .OrderBy(b => b)
                .ToList();

            // Get current branch from workspace repository link
            var currentBranch = wr.BranchName;
            
            // Try to determine default branch from remote branches (heuristic)
            // Actual default branch will be fetched when RefreshBranches is called
            string? defaultBranch = null;
            if (remoteBranches.Contains("main"))
                defaultBranch = "main";
            else if (remoteBranches.Contains("master"))
                defaultBranch = "master";
            else if (remoteBranches.Count > 0)
            {
                // Fallback: use first alphabetically
                defaultBranch = remoteBranches.FirstOrDefault();
            }

            return Results.Ok(new
            {
                localBranches,
                remoteBranches,
                currentBranch,
                defaultBranch
            });
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

    private static async Task<IResult> RefreshBranches(
        RefreshBranchesApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository repoRepository,
        AppDbContext dbContext,
        WorkspaceGitService workspaceGitService,
        IHubContext<WorkspaceSyncHub> hubContext,
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

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (wr == null)
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
            var response = await agentBridge.SendCommandAsync("RefreshBranches", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to refresh branches", statusCode: 500);

            // Parse response and persist branches
            if (response.Data != null)
            {
                var json = response.Data is System.Text.Json.JsonElement je ? je.GetRawText() : System.Text.Json.JsonSerializer.Serialize(response.Data);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                IReadOnlyList<string>? localBranches = null;
                IReadOnlyList<string>? remoteBranches = null;

                if (root.TryGetProperty("localBranches", out var local) && local.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    localBranches = local.EnumerateArray()
                        .Select(b => b.GetString() ?? string.Empty)
                        .Where(b => !string.IsNullOrEmpty(b))
                        .ToList();
                }

                if (root.TryGetProperty("remoteBranches", out var remote) && remote.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    remoteBranches = remote.EnumerateArray()
                        .Select(b => b.GetString() ?? string.Empty)
                        .Where(b => !string.IsNullOrEmpty(b))
                        .ToList();
                }

                // Persist branches using WorkspaceGitService method
                await workspaceGitService.PersistBranchesAsync(wr.WorkspaceRepositoryId, localBranches, remoteBranches, CancellationToken.None);

                // Update default branch if provided in response
                if (root.TryGetProperty("defaultBranch", out var defBranch) && defBranch.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    var defaultBranchName = defBranch.GetString();
                    // Store default branch info if needed (could add to WorkspaceRepositoryLink if needed)
                }

                // Broadcast update to refresh UI
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            }

            return Results.Ok(response.Data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing branches for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while refreshing branches", statusCode: 500);
        }
    }
}

public sealed class RefreshBranchesApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
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
