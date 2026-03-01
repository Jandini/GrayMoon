using GrayMoon.App.Api;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
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
        routes.MapPost("/api/branches/common", GetCommonBranches);
        routes.MapPost("/api/branches/exists-in-workspace", BranchExistsInWorkspace);
        routes.MapPost("/api/branches/create", CreateBranch);
        routes.MapPost("/api/branches/set-upstream", SetUpstreamBranch);
        routes.MapPost("/api/branches/delete", DeleteBranch);
        return routes;
    }

    private static async Task<IResult> BranchExistsInWorkspace(
        BranchExistsInWorkspaceApiRequest? body,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        var branchName = body.BranchName?.Trim();
        if (workspaceId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return Results.BadRequest("workspaceId and branchName are required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found.");

        var count = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Where(wr => dbContext.RepositoryBranches
                .Any(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId
                    && !rb.IsRemote
                    && rb.BranchName == branchName))
            .CountAsync();

        return Results.Ok(new { count });
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

            // Default branch from persisted IsDefault (set when RefreshBranches is called)
            var defaultBranchRow = branches.FirstOrDefault(b => b.IsDefault);
            var defaultBranch = defaultBranchRow?.BranchName;
            if (defaultBranch == null && remoteBranches.Count > 0)
            {
                // Fallback heuristic when never refreshed
                if (remoteBranches.Contains("main")) defaultBranch = "main";
                else if (remoteBranches.Contains("master")) defaultBranch = "master";
                else defaultBranch = remoteBranches.FirstOrDefault();
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
        WorkspaceService workspaceService,
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
        var branchName = body.BranchName;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

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
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("CheckoutBranch", args, CancellationToken.None);

            // Agent always sends success=true when command completes without throwing; actual success is in response.Data
            var checkoutResponse = AgentResponseJson.DeserializeAgentResponse<CheckoutBranchResponse>(response.Data);
            var commandSuccess = checkoutResponse?.Success ?? response.Success;
            var errorMessage = checkoutResponse?.ErrorMessage ?? response.Error ?? "Failed to checkout branch";

            if (!commandSuccess)
                return Results.Ok(new CheckoutBranchApiResult(false, errorMessage));

            // When a remote branch is checked out, persist it as local so it appears in Locals without a fetch
            var localBranchName = checkoutResponse?.CurrentBranch?.Trim();
            if (string.IsNullOrWhiteSpace(localBranchName))
                localBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? branchName.Substring("origin/".Length) : branchName;
            if (!string.IsNullOrWhiteSpace(localBranchName) && wr != null)
            {
                await workspaceGitService.EnsureLocalBranchPersistedAsync(wr.WorkspaceRepositoryId, localBranchName, CancellationToken.None);
                wr.BranchName = localBranchName;
                wr.BranchHasUpstream = null;
                await dbContext.SaveChangesAsync(CancellationToken.None);

                // Refresh branch list from agent so we can set BranchHasUpstream correctly (e.g. main has upstream)
                var refreshArgs = new { workspaceName = workspace.Name, repositoryName = repo.RepositoryName, workspaceRoot };
                var refreshResponse = await agentBridge.SendCommandAsync("RefreshBranches", refreshArgs, CancellationToken.None);
                if (refreshResponse.Success && refreshResponse.Data != null)
                {
                    var branchesResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(refreshResponse.Data);
                    var remoteBranches = branchesResponse?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                    wr.BranchHasUpstream = ComputeBranchHasUpstream(localBranchName, remoteBranches);
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }

            // Broadcast update to refresh UI
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(new CheckoutBranchApiResult(true, null) { CurrentBranch = checkoutResponse?.CurrentBranch });
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
        WorkspaceService workspaceService,
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

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (wr == null)
            return Results.NotFound("Repository is not in the given workspace.");

        if (!agentBridge.IsAgentConnected)
            return Results.Problem("Agent not connected.", statusCode: 503);

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathAsync(CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                currentBranchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("SyncToDefaultBranch", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to sync to default branch", statusCode: 500);

            // Default branch always has upstream
            wr.BranchHasUpstream = true;
            await dbContext.SaveChangesAsync(CancellationToken.None);

            // Sync prunes the previous local branch; remove it from persistence
            var toRemove = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote && rb.BranchName == currentBranchName)
                .ToListAsync(CancellationToken.None);
            if (toRemove.Count > 0)
            {
                dbContext.RepositoryBranches.RemoveRange(toRemove);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

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
        WorkspaceService workspaceService,
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
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("RefreshBranches", args, CancellationToken.None);
            
            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to refresh branches", statusCode: 500);

            // Parse response and persist branches
            var refreshResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(response.Data);
            if (refreshResponse != null)
            {
                var localBranches = refreshResponse.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
                var remoteBranches = refreshResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
                await workspaceGitService.PersistBranchesAsync(wr.WorkspaceRepositoryId, localBranches, remoteBranches, refreshResponse.DefaultBranch, CancellationToken.None);
                wr.BranchHasUpstream = ComputeBranchHasUpstream(wr.BranchName, remoteBranches);
                await dbContext.SaveChangesAsync(CancellationToken.None);
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

    private static async Task<IResult> CreateBranch(
        CreateBranchApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceService workspaceService,
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
        var newBranchName = body.NewBranchName?.Trim();
        var baseBranch = body.BaseBranch;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(newBranchName))
            return Results.BadRequest("workspaceId, repositoryId, and newBranchName are required.");

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
            string baseBranchName;
            if (string.Equals(baseBranch, "__default__", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(baseBranch))
            {
                var defaultRow = await dbContext.RepositoryBranches
                    .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault)
                    .Select(rb => rb.BranchName)
                    .FirstOrDefaultAsync();
                baseBranchName = defaultRow ?? "main";
            }
            else
            {
                baseBranchName = baseBranch;
            }

            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                newBranchName,
                baseBranchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("CreateBranch", args, CancellationToken.None);

            var createResponse = AgentResponseJson.DeserializeAgentResponse<CreateBranchResponse>(response.Data);
            var success = createResponse?.Success ?? response.Success;
            var errorMessage = createResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return Results.Ok(new { success = false, error = errorMessage ?? "Failed to create branch" });

            // Persist new local branch if not exists
            var exists = await dbContext.RepositoryBranches
                .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.BranchName == newBranchName && !rb.IsRemote);
            if (!exists)
            {
                dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                    BranchName = newBranchName,
                    IsRemote = false,
                    LastSeenAt = DateTime.UtcNow,
                    IsDefault = false
                });
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating branch for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while creating branch", statusCode: 500);
        }
    }

    private static async Task<IResult> SetUpstreamBranch(
        SetUpstreamBranchApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceService workspaceService,
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
        var branchName = body.BranchName?.Trim();

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

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
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("SetUpstreamBranch", args, CancellationToken.None);

            var upstreamResponse = AgentResponseJson.DeserializeAgentResponse<SetUpstreamBranchResponse>(response.Data);
            var success = upstreamResponse?.Success ?? response.Success;
            var errorMessage = upstreamResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return Results.Ok(new { success = false, error = errorMessage ?? "Failed to set upstream" });

            // Mark branch as having upstream so Commits badge shows normal state
            wr.BranchHasUpstream = true;
            await dbContext.SaveChangesAsync(CancellationToken.None);

            // Persist the branch as remote (origin) so it appears in Remotes without a fetch
            var remoteBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? branchName : "origin/" + branchName;
            var exists = await dbContext.RepositoryBranches
                .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote && rb.BranchName == remoteBranchName, CancellationToken.None);
            if (!exists)
            {
                dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                    BranchName = remoteBranchName,
                    IsRemote = true,
                    LastSeenAt = DateTime.UtcNow,
                    IsDefault = false
                });
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting upstream for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while setting upstream", statusCode: 500);
        }
    }

    private static async Task<IResult> DeleteBranch(
        DeleteBranchApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceService workspaceService,
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
        var branchName = body.BranchName?.Trim();
        var isRemote = body.IsRemote;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

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

        if (!isRemote)
        {
            var currentBranch = wr.BranchName;
            if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Cannot delete the current branch. Check out another branch first.");
        }

        if (!agentBridge.IsAgentConnected)
            return Results.Problem("Agent not connected.", statusCode: 503);

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName,
                isRemote,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("DeleteBranch", args, CancellationToken.None);

            var deleteResponse = AgentResponseJson.DeserializeAgentResponse<DeleteBranchResponse>(response.Data);
            var success = deleteResponse?.Success ?? response.Success;
            var errorMessage = deleteResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return Results.Ok(new { success = false, error = errorMessage ?? "Failed to delete branch" });

            // Update persistence: remove the deleted branch only (no fetch).
            var toRemove = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote == isRemote && rb.BranchName == branchName)
                .ToListAsync(CancellationToken.None);
            if (toRemove.Count > 0)
            {
                dbContext.RepositoryBranches.RemoveRange(toRemove);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting branch for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while deleting branch", statusCode: 500);
        }
    }
    private static async Task<IResult> GetCommonBranches(
        CommonBranchesApiRequest? body,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        if (workspaceId <= 0)
            return Results.BadRequest("workspaceId is required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found.");

        var links = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Include(wr => wr.Repository)
            .ToListAsync();

        if (links.Count == 0)
        {
            return Results.Ok(new
            {
                commonBranchNames = Array.Empty<string>(),
                defaultDisplayText = "multiple"
            });
        }

        // Local branch names per repo (so we can intersect for "common")
        var branchSets = new List<HashSet<string>>();
        var defaultBranchNames = new List<string>();
        foreach (var wr in links)
        {
            var branches = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote)
                .Select(rb => rb.BranchName)
                .ToListAsync();
            branchSets.Add(branches.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var defaultRow = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault)
                .Select(rb => rb.BranchName)
                .FirstOrDefaultAsync();
            defaultBranchNames.Add(defaultRow ?? "");
        }

        var common = branchSets[0];
        for (var i = 1; i < branchSets.Count; i++)
        {
            common.IntersectWith(branchSets[i]);
        }

        // Default option: one common default (e.g. main [default]) or "multiple [default]" when repos have different defaults
        var distinctDefaults = defaultBranchNames.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defaultDisplayText = distinctDefaults.Count == 1 ? distinctDefaults[0] : "multiple";

        // All other branches common across every repo go in the list; exclude the single default so it appears only as the first option
        if (distinctDefaults.Count == 1)
            common.Remove(distinctDefaults[0]);
        var commonBranchNames = common.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

        return Results.Ok(new
        {
            commonBranchNames,
            defaultDisplayText
        });
    }

    /// <summary>Returns true if the current branch has a matching remote (e.g. origin/branchName), false otherwise. Returns null when unknown (no branch name or no remote list).</summary>
    private static bool? ComputeBranchHasUpstream(string? currentBranchName, IReadOnlyList<string>? remoteBranches)
    {
        if (string.IsNullOrWhiteSpace(currentBranchName) || remoteBranches == null || remoteBranches.Count == 0)
            return null;
        var branch = currentBranchName.Trim();
        var hasUpstream = remoteBranches.Any(r => !string.IsNullOrEmpty(r) &&
            (string.Equals(r, "origin/" + branch, StringComparison.OrdinalIgnoreCase) || r.EndsWith("/" + branch, StringComparison.OrdinalIgnoreCase)));
        return hasUpstream;
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

/// <summary>API response for POST /api/branches/checkout (serialized to camelCase).</summary>
public sealed class CheckoutBranchApiResult
{
    public bool Success { get; set; }
    public string? CurrentBranch { get; set; }
    public string? ErrorMessage { get; set; }

    public CheckoutBranchApiResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }
}

public sealed class SyncToDefaultBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? CurrentBranchName { get; set; }
}

public sealed class CommonBranchesApiRequest
{
    public int WorkspaceId { get; set; }
}

public sealed class BranchExistsInWorkspaceApiRequest
{
    public int WorkspaceId { get; set; }
    public string? BranchName { get; set; }
}

public sealed class CreateBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? NewBranchName { get; set; }
    public string? BaseBranch { get; set; }
}

public sealed class SetUpstreamBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
}

public sealed class DeleteBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
    public bool IsRemote { get; set; }
}

