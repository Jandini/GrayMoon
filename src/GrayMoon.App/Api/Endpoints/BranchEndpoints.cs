using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
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
        routes.MapPost("/api/branches/update-from-default", UpdateBranchFromDefault);
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
            // Read branches and tags from database
            var rows = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .ToListAsync();

            var localBranches = rows
                .Where(b => !b.IsRemote && !b.IsTag)
                .Select(b => b.BranchName)
                .OrderBy(b => b)
                .ToList();

            var remoteBranches = rows
                .Where(b => b.IsRemote && !b.IsTag)
                .Select(b => b.BranchName)
                .OrderBy(b => b)
                .ToList();

            // Tags are persisted with SortIndex matching the agent's "newest first" (creator-date descending) order, so order by that here.
            var tags = rows
                .Where(b => b.IsTag)
                .OrderBy(b => b.SortIndex)
                .ThenBy(b => b.BranchName)
                .Select(b => b.BranchName)
                .ToList();

            // Get current branch (null when on a tag) from workspace repository link
            var currentBranch = wr.BranchName;
            var currentTag = wr.CheckedOutTag;

            // Default branch from persisted IsDefault (set when RefreshBranches is called)
            var defaultBranchRow = rows.FirstOrDefault(b => b.IsDefault && !b.IsTag);
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
                defaultBranch,
                tags,
                currentTag
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
        var branchName = body.BranchName?.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) == true
            ? body.BranchName.Substring("origin/".Length)
            : body.BranchName;
        var isTag = body.IsTag;

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

            if (isTag)
            {
                var tagArgs = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    tagName = branchName,
                    workspaceRoot
                };
                var tagResponse = await agentBridge.SendCommandAsync("CheckoutTag", tagArgs, CancellationToken.None);
                var tagCheckout = AgentResponseJson.DeserializeAgentResponse<CheckoutTagResponse>(tagResponse.Data);
                var tagSuccess = tagCheckout?.Success ?? tagResponse.Success;
                var tagError = tagCheckout?.ErrorMessage ?? tagResponse.Error ?? "Failed to checkout tag";

                if (!tagSuccess)
                    return Results.Ok(new CheckoutBranchApiResult(false, tagError));

                // Persist pinned-to-tag state immediately so the UI gating kicks in even before the
                // checkout hook arrives. Clear branch-only fields to avoid stale push/divergence badges.
                wr.CheckedOutTag = tagCheckout?.CurrentTag ?? branchName.Trim();
                wr.BranchName = null;
                wr.BranchHasUpstream = null;
                wr.OutgoingCommits = null;
                wr.IncomingCommits = null;
                wr.DefaultBranchBehindCommits = null;
                wr.DefaultBranchAheadCommits = null;
                await dbContext.SaveChangesAsync(CancellationToken.None);

                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

                return Results.Ok(new CheckoutBranchApiResult(true, null) { CurrentBranch = null });
            }

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryId = repo.RepositoryId,
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
                // Switching to a real branch clears any pinned tag state.
                wr.CheckedOutTag = null;
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            // Broadcast update to refresh UI (branch name). BranchHasUpstream and commit counts will be updated when the checkout hook notify runs (CheckoutHookSync → SyncCommand).
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
        WorkspacePullRequestRepository workspacePullRequestRepository,
        WorkspacePullRequestService workspacePullRequestService,
        AppDbContext dbContext,
        WorkspaceGitService workspaceGitService,
        IHubContext<WorkspaceSyncHub> hubContext,
        ConnectorHealthService connectorHealthService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Branches");
        if (body == null)
            return Results.BadRequest("Request body is required.");

        var workspaceId = body.WorkspaceId;
        var repositoryId = body.RepositoryId;
        var currentBranchName = body.CurrentBranchName;
        var deleteRemoteBranch = body.DeleteRemoteBranch;

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
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, CancellationToken.None);

            await workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: true, CancellationToken.None);
            var prByRepo = await workspacePullRequestRepository.GetByWorkspaceIdAsync(workspaceId, CancellationToken.None);
            var forceDeleteLocalBranch = body.AllowForceDeleteLocalBranch
                && prByRepo.TryGetValue(repositoryId, out var pr)
                && (pr?.IsMerged == true || pr?.IsClosed == true);

            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                currentBranchName,
                bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                workspaceRoot,
                forceDeleteLocalBranch,
                deleteRemoteBranch
            };
            var response = await agentBridge.SendCommandAsync("SyncToDefaultBranch", args, CancellationToken.None);

            // Agent sends success=true when command completes without throwing; actual success is in response.Data
            var syncResponse = AgentResponseJson.DeserializeAgentResponse<SyncToDefaultBranchResponse>(response.Data);
            var commandSuccess = syncResponse?.Success ?? response.Success;
            var errorMessage = syncResponse?.ErrorMessage ?? response.Error ?? "Failed to sync to default branch";

            if (!commandSuccess)
                return Results.Problem(errorMessage, statusCode: 500);

            // Persist the full branch state returned by the agent (fetch --prune was run, so stale remote branches are gone)
            if (syncResponse?.LocalBranches != null)
            {
                var localBranches = syncResponse.LocalBranches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                var remoteBranches = syncResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
                var tags = syncResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                await workspaceGitService.PersistBranchesAsync(
                    wr.WorkspaceRepositoryId,
                    localBranches,
                    remoteBranches,
                    syncResponse.DefaultBranch,
                    tags,
                    syncResponse.CurrentTag,
                    CancellationToken.None);
            }
            else
            {
                // Fallback for older agents: prune only the previous local branch
                var toRemove = await dbContext.RepositoryBranches
                    .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsRemote && rb.BranchName == currentBranchName)
                    .ToListAsync(CancellationToken.None);
                if (toRemove.Count > 0)
                {
                    dbContext.RepositoryBranches.RemoveRange(toRemove);
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
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
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("RefreshBranches", args, CancellationToken.None);

            var refreshResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(response.Data);
            if (refreshResponse?.Success == false)
                return Results.Problem(refreshResponse.ErrorMessage ?? "Failed to refresh branches", statusCode: 500);

            if (!response.Success)
                return Results.Problem(response.Error ?? "Failed to refresh branches", statusCode: 500);

            // Parse response and persist branches
            if (refreshResponse != null)
            {
                var localBranches = refreshResponse.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
                var remoteBranches = refreshResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
                var tags = refreshResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                await workspaceGitService.PersistBranchesAsync(
                    wr.WorkspaceRepositoryId,
                    localBranches,
                    remoteBranches,
                    refreshResponse.DefaultBranch,
                    tags,
                    refreshResponse.CurrentTag,
                    CancellationToken.None);
                if (string.IsNullOrWhiteSpace(refreshResponse.CurrentTag))
                {
                    var hasUpstream = ComputeBranchHasUpstream(refreshResponse.CurrentBranch, remoteBranches);
                    if (hasUpstream.HasValue)
                    {
                        wr.BranchHasUpstream = hasUpstream.Value;
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                    }
                }
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
                workspaceRoot,
                repositoryId
            };
            var response = await agentBridge.SendCommandAsync("SetUpstreamBranch", args, CancellationToken.None);

            var upstreamResponse = AgentResponseJson.DeserializeAgentResponse<SetUpstreamBranchResponse>(response.Data);
            var success = upstreamResponse?.Success ?? response.Success;
            var errorMessage = upstreamResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return Results.Ok(new { success = false, error = errorMessage ?? "Failed to set upstream" });

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
        var force = body.Force;

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
                force,
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

            // If the deleted branch was the current branch's remote, mark as no upstream.
            if (isRemote && string.Equals(wr.BranchName, branchName, StringComparison.OrdinalIgnoreCase))
            {
                wr.BranchHasUpstream = false;
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
    private static async Task<IResult> UpdateBranchFromDefault(
        UpdateBranchFromDefaultApiRequest? body,
        IAgentBridge agentBridge,
        WorkspaceService workspaceService,
        WorkspaceRepository workspaceRepository,
        GitHubRepositoryRepository repoRepository,
        AppDbContext dbContext,
        IHubContext<WorkspaceSyncHub> hubContext,
        ConnectorHealthService connectorHealthService,
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
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, CancellationToken.None);

            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, CancellationToken.None);
            var defaultBranchName = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault && !rb.IsTag)
                .Select(rb => rb.BranchName)
                .FirstOrDefaultAsync(CancellationToken.None) ?? "main";

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                currentBranchName = wr.BranchName,
                defaultBranchName,
                bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("UpdateBranchFromDefault", args, CancellationToken.None);

            var updateResponse = AgentResponseJson.DeserializeAgentResponse<UpdateBranchFromDefaultResponse>(response.Data);
            var commandSuccess = updateResponse?.Success ?? response.Success;

            if (!commandSuccess && updateResponse?.HasConflicts != true)
            {
                var errorMessage = updateResponse?.ErrorMessage ?? response.Error ?? "Failed to update branch";
                return Results.Problem(errorMessage, statusCode: 500);
            }

            // On clean merge: persist updated commit counts returned by the agent.
            if (commandSuccess && updateResponse != null)
            {
                if (updateResponse.OutgoingCommits.HasValue)
                    wr.OutgoingCommits = updateResponse.OutgoingCommits.Value;
                if (updateResponse.IncomingCommits.HasValue)
                    wr.IncomingCommits = updateResponse.IncomingCommits.Value;
                if (updateResponse.DefaultBranchBehind.HasValue)
                    wr.DefaultBranchBehindCommits = updateResponse.DefaultBranchBehind.Value;
                if (updateResponse.DefaultBranchAhead.HasValue)
                    wr.DefaultBranchAheadCommits = updateResponse.DefaultBranchAhead.Value;
                await dbContext.SaveChangesAsync(CancellationToken.None);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            }

            return Results.Ok(new
            {
                success = commandSuccess,
                hasConflicts = updateResponse?.HasConflicts ?? false,
                conflictFiles = updateResponse?.ConflictFiles ?? Array.Empty<string>()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating branch from default for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while updating branch from default", statusCode: 500);
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
                commonLocalBranchNames = Array.Empty<string>(),
                commonRemoteBranchNames = Array.Empty<string>(),
                defaultDisplayText = "multiple"
            });
        }

        // Branch names per repo (local and remote separately so UI can combine without collapsing).
        var localBranchSets = new List<HashSet<string>>();
        var remoteBranchSets = new List<HashSet<string>>();
        var defaultBranchNames = new List<string>();
        foreach (var wr in links)
        {
            var branches = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .Select(rb => new { rb.BranchName, rb.IsRemote, rb.IsDefault })
                .ToListAsync();

            localBranchSets.Add(
                branches
                    .Where(b => !b.IsRemote)
                    .Select(b => b.BranchName)
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));

            remoteBranchSets.Add(
                branches
                    .Where(b => b.IsRemote)
                    .Select(b => b.BranchName)
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));

            var defaultRow = branches.FirstOrDefault(b => b.IsDefault)?.BranchName;
            defaultBranchNames.Add(defaultRow ?? "");
        }

        var commonLocal = localBranchSets[0];
        for (var i = 1; i < localBranchSets.Count; i++)
        {
            commonLocal.IntersectWith(localBranchSets[i]);
        }

        var commonRemote = remoteBranchSets[0];
        for (var i = 1; i < remoteBranchSets.Count; i++)
        {
            commonRemote.IntersectWith(remoteBranchSets[i]);
        }

        // Default option: one common default (e.g. main [default]) or "multiple [default]" when repos have different defaults
        var distinctDefaults = defaultBranchNames.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defaultDisplayText = distinctDefaults.Count == 1 ? distinctDefaults[0] : "multiple";

        // All other branches common across every repo go in the list; exclude the single default so it appears only as the first option
        if (distinctDefaults.Count == 1)
            commonLocal.Remove(distinctDefaults[0]);

        var commonLocalBranchNames = commonLocal.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
        var commonRemoteBranchNames = commonRemote.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

        return Results.Ok(new
        {
            // Keep legacy field populated with local common branches for backwards compatibility.
            commonBranchNames = commonLocalBranchNames,
            commonLocalBranchNames,
            commonRemoteBranchNames,
            defaultDisplayText
        });
    }

    /// <summary>Returns true if the current branch has a matching remote (e.g. origin/branchName or branchName), false otherwise. Returns null when unknown (no branch name or no remote list).</summary>
    private static bool? ComputeBranchHasUpstream(string? currentBranchName, IReadOnlyList<string>? remoteBranches)
    {
        if (string.IsNullOrWhiteSpace(currentBranchName) || remoteBranches == null || remoteBranches.Count == 0)
            return null;
        var branch = currentBranchName.Trim();
        var hasUpstream = remoteBranches.Any(r => !string.IsNullOrEmpty(r) &&
            (string.Equals(r, branch, StringComparison.OrdinalIgnoreCase)
             || string.Equals(r, "origin/" + branch, StringComparison.OrdinalIgnoreCase)
             || r.EndsWith("/" + branch, StringComparison.OrdinalIgnoreCase)));
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
    /// <summary>When true, <see cref="BranchName"/> is treated as a tag name and the agent dispatches a CheckoutTag command (detached HEAD). Defaults to false for backward compatibility.</summary>
    public bool IsTag { get; set; }
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
    public bool DeleteRemoteBranch { get; set; }
    public bool AllowForceDeleteLocalBranch { get; set; } = true;
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
    /// <summary>When true, local delete uses git branch -D (after user confirmed not-fully-merged warning).</summary>
    public bool Force { get; set; }
}

public sealed class UpdateBranchFromDefaultApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
}

/// <summary>Mirrors UpdateBranchFromDefaultResponse from the Agent for JSON deserialization on the App side.</summary>
public sealed class UpdateBranchFromDefaultResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("hasConflicts")]
    public bool HasConflicts { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("conflictFiles")]
    public IReadOnlyList<string>? ConflictFiles { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }
}

