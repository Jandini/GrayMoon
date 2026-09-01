using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceBranchOperations(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repoRepository,
    AppDbContext dbContext,
    IDbContextFactory<AppDbContext> dbContextFactory,
    WorkspaceGitService workspaceGitService,
    WorkspaceRepositoryStateWriter stateWriter,
    IHubContext<WorkspaceSyncHub> hubContext,
    ConnectorHealthService connectorHealthService,
    WorkspaceBranchUpdateHandler updateHandler,
    ILogger<WorkspaceBranchOperations> logger) : IWorkspaceBranchOperations
{
    public async Task<BranchHttpOutcome> GetBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: false, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        try
        {
            // Fresh context: the circuit-scoped dbContext may still track a WorkspaceRepositoryLink
            // written by an earlier open (e.g. feature/x) after merge+sync persisted main elsewhere.
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var wr = await db.WorkspaceRepositories
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
            if (wr == null)
                return BranchHttpOutcome.NotFound("Repository is not in the given workspace.");

            var rows = await db.RepositoryBranches
                .AsNoTracking()
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId)
                .ToListAsync(cancellationToken);

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

            // Tags are persisted with SortIndex matching the agent's "newest first" (creator-date descending) order.
            var tags = rows
                .Where(b => b.IsTag)
                .OrderBy(b => b.SortIndex)
                .ThenBy(b => b.BranchName)
                .Select(b => b.BranchName)
                .ToList();

            var currentBranch = wr.BranchName;
            var currentTag = wr.CheckedOutTag;

            var defaultBranchRow = rows.FirstOrDefault(b => b.IsDefault && !b.IsTag);
            var defaultBranch = defaultBranchRow?.BranchName;
            if (defaultBranch == null && remoteBranches.Count > 0)
            {
                if (remoteBranches.Contains("main")) defaultBranch = "main";
                else if (remoteBranches.Contains("master")) defaultBranch = "master";
                else defaultBranch = remoteBranches.FirstOrDefault();
            }

            return BranchHttpOutcome.Ok(new WorkspaceBranchesSnapshot
            {
                LocalBranches = localBranches,
                RemoteBranches = remoteBranches,
                CurrentBranch = currentBranch,
                DefaultBranch = defaultBranch,
                Tags = tags,
                CurrentTag = currentTag
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting branches for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while getting branches", 500);
        }
    }

    public async Task<BranchHttpOutcome> RefreshBranchesAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: true, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var workspace = resolved.Workspace!;
        var repo = resolved.Repo!;
        var wr = resolved.Link!;

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("RefreshBranches", args, cancellationToken);

            var refreshResponse = AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(response.Data);
            if (refreshResponse?.Success == false)
                return BranchHttpOutcome.Problem(refreshResponse.ErrorMessage ?? "Failed to refresh branches", 500);

            if (!response.Success)
                return BranchHttpOutcome.Problem(response.Error ?? "Failed to refresh branches", 500);

            if (refreshResponse != null)
            {
                var localBranches = refreshResponse.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? [];
                var remoteBranches = refreshResponse.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? [];
                var tags = refreshResponse.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                await workspaceGitService.PersistBranchesAsync(
                    wr.WorkspaceRepositoryId,
                    localBranches,
                    remoteBranches,
                    refreshResponse.DefaultBranch,
                    tags,
                    refreshResponse.CurrentTag,
                    cancellationToken);
                // Only the agent's git-config probe may set this. Matching the branch name against the
                // remote list marked any same-named branch as having an upstream, which showed the upstream
                // badge on branches that had never been pushed.
                if (refreshResponse.UpstreamProbed && string.IsNullOrWhiteSpace(refreshResponse.CurrentTag))
                {
                    wr.BranchHasUpstream = refreshResponse.HasUpstream;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
            }

            return BranchHttpOutcome.Ok(response.Data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing branches for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while refreshing branches", 500);
        }
    }

    public async Task<BranchHttpOutcome> CheckoutAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        bool isTag,
        CancellationToken cancellationToken = default)
    {
        branchName = branchName?.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) == true
            ? branchName.Substring("origin/".Length)
            : branchName;

        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return BranchHttpOutcome.BadRequest("workspaceId, repositoryId, and branchName are required.");

        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: true, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var workspace = resolved.Workspace!;
        var repo = resolved.Repo!;
        var wr = resolved.Link!;

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

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
                var tagResponse = await agentBridge.SendCommandAsync("CheckoutTag", tagArgs, cancellationToken);
                var tagCheckout = AgentResponseJson.DeserializeAgentResponse<CheckoutTagResponse>(tagResponse.Data);
                var tagSuccess = tagCheckout?.Success ?? tagResponse.Success;
                var tagError = tagCheckout?.ErrorMessage ?? tagResponse.Error ?? "Failed to checkout tag";

                if (!tagSuccess)
                    return BranchHttpOutcome.Ok(new CheckoutBranchApiResult(false, tagError));

                await stateWriter.ApplyAsync(workspaceId, repositoryId, new RepositoryStateSnapshot
                {
                    CheckedOutTag = tagCheckout?.CurrentTag ?? branchName.Trim(),
                    IdentityProbed = true,
                }, new RepositoryStateWriteOptions { ReconcilePullRequest = true });

                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

                return BranchHttpOutcome.Ok(new CheckoutBranchApiResult(true, null) { CurrentBranch = null });
            }

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                branchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("CheckoutBranch", args, cancellationToken);

            var checkoutResponse = AgentResponseJson.DeserializeAgentResponse<CheckoutBranchResponse>(response.Data);
            var commandSuccess = checkoutResponse?.Success ?? response.Success;
            var errorMessage = checkoutResponse?.ErrorMessage ?? response.Error ?? "Failed to checkout branch";

            if (!commandSuccess)
                return BranchHttpOutcome.Ok(new CheckoutBranchApiResult(false, errorMessage));

            var localBranchName = checkoutResponse?.CurrentBranch?.Trim();
            if (string.IsNullOrWhiteSpace(localBranchName))
                localBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                    ? branchName.Substring("origin/".Length)
                    : branchName;
            if (!string.IsNullOrWhiteSpace(localBranchName))
            {
                await workspaceGitService.EnsureLocalBranchPersistedAsync(wr.WorkspaceRepositoryId, localBranchName, cancellationToken);
                await stateWriter.ApplyAsync(workspaceId, repositoryId, new RepositoryStateSnapshot
                {
                    BranchName = localBranchName,
                    IdentityProbed = true,
                }, new RepositoryStateWriteOptions { ReconcilePullRequest = true });
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            return BranchHttpOutcome.Ok(new CheckoutBranchApiResult(true, null) { CurrentBranch = checkoutResponse?.CurrentBranch });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking out branch for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while checking out branch", 500);
        }
    }

    public async Task<BranchHttpOutcome> SyncToDefaultAsync(
        int workspaceId,
        int repositoryId,
        string? currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(currentBranchName))
            return BranchHttpOutcome.BadRequest("workspaceId, repositoryId, and currentBranchName are required.");

        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: true, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var repo = resolved.Repo!;

        try
        {
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

            var (success, errorMessage) = await workspaceGitService.SyncToDefaultDirectAsync(
                workspaceId,
                repositoryId,
                currentBranchName,
                deleteRemoteBranch,
                allowForceDeleteLocalBranch,
                cancellationToken);

            if (!success)
                return BranchHttpOutcome.Problem(errorMessage ?? "Failed to sync to default branch", 500);

            await workspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);

            return BranchHttpOutcome.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while syncing to default branch", 500);
        }
    }

    public async Task<BranchHttpOutcome> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
            return BranchHttpOutcome.BadRequest("workspaceId is required.");

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceExists = await db.Workspaces
            .AsNoTracking()
            .AnyAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        if (!workspaceExists)
            return BranchHttpOutcome.NotFound("Workspace not found.");

        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        // Repos checked out on a tag do not participate in the common-branch intersection.
        links = links.Where(wr => !wr.IsOnTag).ToList();

        if (links.Count == 0)
        {
            return BranchHttpOutcome.Ok(new CommonBranchesApiResult
            {
                CommonBranchNames = [],
                CommonLocalBranchNames = [],
                CommonRemoteBranchNames = [],
                DefaultDisplayText = "multiple"
            });
        }

        var localBranchSets = new List<HashSet<string>>();
        var remoteBranchSets = new List<HashSet<string>>();
        var defaultBranchNames = new List<string>();
        foreach (var wr in links)
        {
            var branches = await db.RepositoryBranches
                .AsNoTracking()
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && !rb.IsTag)
                .Select(rb => new { rb.BranchName, rb.IsRemote, rb.IsDefault })
                .ToListAsync(cancellationToken);

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
            commonLocal.IntersectWith(localBranchSets[i]);

        var commonRemote = remoteBranchSets[0];
        for (var i = 1; i < remoteBranchSets.Count; i++)
            commonRemote.IntersectWith(remoteBranchSets[i]);

        var distinctDefaults = defaultBranchNames
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defaultDisplayText = distinctDefaults.Count == 1 ? distinctDefaults[0] : "multiple";

        if (distinctDefaults.Count == 1)
            commonLocal.Remove(distinctDefaults[0]);

        var commonLocalBranchNames = commonLocal.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
        var commonRemoteBranchNames = commonRemote.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

        return BranchHttpOutcome.Ok(new CommonBranchesApiResult
        {
            CommonBranchNames = commonLocalBranchNames,
            CommonLocalBranchNames = commonLocalBranchNames,
            CommonRemoteBranchNames = commonRemoteBranchNames,
            DefaultDisplayText = defaultDisplayText
        });
    }

    public async Task<BranchHttpOutcome> CountReposWithLocalBranchAsync(int workspaceId, string? branchName, CancellationToken cancellationToken = default)
    {
        branchName = branchName?.Trim();
        if (workspaceId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return BranchHttpOutcome.BadRequest("workspaceId and branchName are required.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return BranchHttpOutcome.NotFound("Workspace not found.");

        var count = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Where(wr => dbContext.RepositoryBranches
                .Any(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId
                    && !rb.IsRemote
                    && rb.BranchName == branchName))
            .CountAsync(cancellationToken);

        return BranchHttpOutcome.Ok(new BranchExistsCount { Count = count });
    }

    public async Task<BranchHttpOutcome> CreateBranchAsync(
        int workspaceId,
        int repositoryId,
        string? newBranchName,
        string? baseBranch,
        CancellationToken cancellationToken = default)
    {
        newBranchName = newBranchName?.Trim();
        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(newBranchName))
            return BranchHttpOutcome.BadRequest("workspaceId, repositoryId, and newBranchName are required.");

        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: true, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var workspace = resolved.Workspace!;
        var repo = resolved.Repo!;
        var wr = resolved.Link!;

        try
        {
            string baseBranchName;
            if (string.Equals(baseBranch, "__default__", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(baseBranch))
            {
                var defaultRow = await dbContext.RepositoryBranches
                    .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault)
                    .Select(rb => rb.BranchName)
                    .FirstOrDefaultAsync(cancellationToken);
                baseBranchName = defaultRow ?? "main";
            }
            else
            {
                baseBranchName = baseBranch;
            }

            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                newBranchName,
                baseBranchName,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("CreateBranch", args, cancellationToken);

            var createResponse = AgentResponseJson.DeserializeAgentResponse<CreateBranchResponse>(response.Data);
            var success = createResponse?.Success ?? response.Success;
            var errorMessage = createResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return BranchHttpOutcome.Ok(new CreateBranchApiResult { Success = false, Error = errorMessage ?? "Failed to create branch" });

            var exists = await dbContext.RepositoryBranches
                .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.BranchName == newBranchName && !rb.IsRemote, cancellationToken);
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
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            return BranchHttpOutcome.Ok(new CreateBranchApiResult { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating branch for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while creating branch", 500);
        }
    }

    public async Task<BranchHttpOutcome> SetUpstreamAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        CancellationToken cancellationToken = default)
    {
        branchName = branchName?.Trim();
        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return BranchHttpOutcome.BadRequest("workspaceId, repositoryId, and branchName are required.");

        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: true, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var workspace = resolved.Workspace!;
        var repo = resolved.Repo!;
        var wr = resolved.Link!;

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName,
                workspaceRoot,
                repositoryId
            };
            var response = await agentBridge.SendCommandAsync("SetUpstreamBranch", args, cancellationToken);

            var upstreamResponse = AgentResponseJson.DeserializeAgentResponse<SetUpstreamBranchResponse>(response.Data);
            var success = upstreamResponse?.Success ?? response.Success;
            var errorMessage = upstreamResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return BranchHttpOutcome.Ok(new CreateBranchApiResult { Success = false, Error = errorMessage ?? "Failed to set upstream" });

            var remoteBranchName = branchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? branchName : "origin/" + branchName;
            var exists = await dbContext.RepositoryBranches
                .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote && rb.BranchName == remoteBranchName, cancellationToken);
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
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            return BranchHttpOutcome.Ok(new CreateBranchApiResult { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting upstream for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while setting upstream", 500);
        }
    }

    public async Task<BranchHttpOutcome> DeleteBranchAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        bool isRemote,
        bool force,
        CancellationToken cancellationToken = default)
    {
        branchName = branchName?.Trim();
        if (workspaceId <= 0 || repositoryId <= 0 || string.IsNullOrWhiteSpace(branchName))
            return BranchHttpOutcome.BadRequest("workspaceId, repositoryId, and branchName are required.");

        var resolved = await TryResolveLinkedRepoAsync(workspaceId, repositoryId, requireAgent: false, cancellationToken);
        if (resolved.Error != null)
            return resolved.Error;

        var workspace = resolved.Workspace!;
        var repo = resolved.Repo!;
        var wr = resolved.Link!;

        if (!isRemote)
        {
            var currentBranch = wr.BranchName;
            if (string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
                return BranchHttpOutcome.BadRequest("Cannot delete the current branch. Check out another branch first.");
        }

        if (!agentBridge.IsAgentConnected)
            return BranchHttpOutcome.Problem("Agent not connected.", 503);

        try
        {
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                branchName,
                isRemote,
                force,
                workspaceRoot
            };
            var response = await agentBridge.SendCommandAsync("DeleteBranch", args, cancellationToken);

            var deleteResponse = AgentResponseJson.DeserializeAgentResponse<DeleteBranchResponse>(response.Data);
            var success = deleteResponse?.Success ?? response.Success;
            var errorMessage = deleteResponse?.ErrorMessage ?? response.Error;

            if (!success)
                return BranchHttpOutcome.Ok(new DeleteBranchApiResult { Success = false, Error = errorMessage ?? "Failed to delete branch" });

            var toRemove = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote == isRemote && rb.BranchName == branchName)
                .ToListAsync(cancellationToken);
            if (toRemove.Count > 0)
            {
                dbContext.RepositoryBranches.RemoveRange(toRemove);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (isRemote && string.Equals(wr.BranchName, branchName, StringComparison.OrdinalIgnoreCase))
            {
                wr.BranchHasUpstream = false;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            return BranchHttpOutcome.Ok(new DeleteBranchApiResult { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting branch for repository {RepositoryId}", repositoryId);
            return BranchHttpOutcome.Problem("An error occurred while deleting branch", 500);
        }
    }

    public async Task<BranchHttpOutcome> UpdateBranchFromDefaultAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0 || repositoryId <= 0)
            return BranchHttpOutcome.BadRequest("workspaceId and repositoryId are required.");

        var result = await updateHandler.UpdateBranchFromDefaultAsync(workspaceId, repositoryId, cancellationToken);

        if (result.Success || result.HasConflicts)
        {
            return BranchHttpOutcome.Ok(new UpdateBranchFromDefaultHttpBody
            {
                Success = result.Success,
                HasConflicts = result.HasConflicts,
                ConflictFiles = result.ConflictFiles
            });
        }

        var err = result.ErrorMessage ?? "Failed to update branch";
        if (err.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return BranchHttpOutcome.NotFound(err);
        if (err.Contains("Agent not connected", StringComparison.OrdinalIgnoreCase))
            return BranchHttpOutcome.Problem("Agent not connected.", 503);
        if (err.Contains("unexpected", StringComparison.OrdinalIgnoreCase))
            return BranchHttpOutcome.Problem("An error occurred while updating branch from default", 500);

        return BranchHttpOutcome.Problem(err, 500);
    }

    private async Task<(BranchHttpOutcome? Error, Workspace? Workspace, Repository? Repo, WorkspaceRepositoryLink? Link)> TryResolveLinkedRepoAsync(
        int workspaceId,
        int repositoryId,
        bool requireAgent,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (BranchHttpOutcome.NotFound("Workspace not found."), null, null, null);

        var repo = await repoRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
            return (BranchHttpOutcome.NotFound("Repository not found."), null, null, null);

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return (BranchHttpOutcome.NotFound("Repository is not in the given workspace."), null, null, null);

        if (requireAgent && !agentBridge.IsAgentConnected)
            return (BranchHttpOutcome.Problem("Agent not connected.", 503), null, null, null);

        return (null, workspace, repo, wr);
    }
}
