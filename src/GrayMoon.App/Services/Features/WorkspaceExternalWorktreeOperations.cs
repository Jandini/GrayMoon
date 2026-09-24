using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Jobs;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceExternalWorktreeOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceOperationLock operationLock,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    IAgentBridge agentBridge,
    ILogger<WorkspaceExternalWorktreeOperations> logger) : IWorkspaceExternalWorktreeOperations
{
    internal const string FeatureOwnedError = "This worktree belongs to a GrayMoon Feature. Use Remove Feature instead.";
    internal const string InspectFailedError = "Could not inspect the external worktree.";

    public async Task<ExternalWorktreeCleanupPlan> AnalyzeExternalWorktreeCleanupAsync(
        int workspaceId,
        int workspaceRepositoryId,
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var link = await db.WorkspaceRepositories
                .AsNoTracking()
                .Include(l => l.Repository)
                .FirstOrDefaultAsync(
                    l => l.WorkspaceId == workspaceId && l.WorkspaceRepositoryId == workspaceRepositoryId,
                    cancellationToken);
            if (link?.Repository is null)
            {
                return new ExternalWorktreeCleanupPlan
                {
                    Success = false,
                    Error = "Repository not found.",
                    WorktreePath = worktreePath
                };
            }

            // Refuse GrayMoon-owned Feature paths - those use RemoveFeature.
            // Path normalize/compare must be client-side: EF cannot translate Equals(StringComparison).
            var featurePaths = await db.WorkspaceFeatureRepositories.AsNoTracking()
                .Where(r => r.WorkspaceRepositoryId == workspaceRepositoryId && r.WorktreePath != null)
                .Select(r => r.WorktreePath!)
                .ToListAsync(cancellationToken);
            var owned = featurePaths.Any(p => GitWorktreeOccupancy.PathsEqual(p, worktreePath));
            if (owned)
            {
                return new ExternalWorktreeCleanupPlan
                {
                    Success = false,
                    Error = FeatureOwnedError,
                    WorktreePath = worktreePath
                };
            }

            var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
            var mainPath = await pathResolver.GetRepositoryPathAsync(special, workspaceRepositoryId, cancellationToken);
            var listResp = await agentBridge.SendCommandAsync(
                AgentHubMethods.ListGitWorktrees,
                new { mainRepositoryPath = mainPath },
                cancellationToken);

            GitWorktreeInfo? match = null;
            if (listResp.Success && listResp.Data != null)
            {
                var payload = AgentResponseJson.DeserializeAgentResponse<ListWorktreesAgentResponse>(listResp.Data);
                match = GitWorktreeOccupancy.FindByPath(payload?.Worktrees, worktreePath);
            }

            var exists = Directory.Exists(worktreePath);
            var dirty = false; // Agent-side dirty probe deferred; force path still requires explicit auth.
            return new ExternalWorktreeCleanupPlan
            {
                Success = true,
                RepositoryName = link.Repository.RepositoryName,
                BranchName = match?.BranchName,
                WorktreePath = worktreePath,
                HeadCommit = match?.HeadSha,
                WorktreeExists = exists || match != null,
                IsDirty = dirty,
                CanRemoveNormally = exists && !dirty,
                RequiresForce = dirty,
                Summary = exists
                    ? $"External worktree at {worktreePath}"
                    : "Worktree path not found on disk"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AnalyzeExternalWorktreeCleanup failed for {WorktreePath}", worktreePath);
            return new ExternalWorktreeCleanupPlan
            {
                Success = false,
                Error = InspectFailedError,
                WorktreePath = worktreePath
            };
        }
    }

    public async Task<OperationResult> RemoveExternalWorktreeAsync(
        int workspaceId,
        int workspaceRepositoryId,
        string worktreePath,
        ExternalWorktreeCleanupOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await AnalyzeExternalWorktreeCleanupAsync(workspaceId, workspaceRepositoryId, worktreePath, cancellationToken);
        if (!plan.Success)
        {
            // Feature-owned paths must never be force-removed via this API.
            if (string.Equals(plan.Error, FeatureOwnedError, StringComparison.Ordinal))
                return OperationResult.Fail(plan.Error);

            // Stray / uninspectable worktrees: allow an explicitly authorized force remove.
            if (!options.AllowForceRemoveDirty)
                return OperationResult.Fail(plan.Error ?? "Analyze failed.");
        }
        else if (plan.RequiresForce && !options.AllowForceRemoveDirty)
        {
            return OperationResult.Fail("Worktree is dirty; authorize force removal explicitly.");
        }

        var tcs = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            workspaceId,
            "remove-external-worktree",
            WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            "Removing external worktree...",
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    progress?.Report(new OperationProgress(op.DisplayMessage));
                    var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, linked.Token);
                    var mainPath = await pathResolver.GetRepositoryPathAsync(special, workspaceRepositoryId, linked.Token);
                    var force = options.AllowForceRemoveDirty || plan.RequiresForce || !plan.Success;
                    var resp = await agentBridge.SendCommandAsync(
                        AgentHubMethods.RemoveGitWorktree,
                        new { mainRepositoryPath = mainPath, worktreePath, force },
                        linked.Token);
                    if (!resp.Success)
                    {
                        tcs.TrySetResult(OperationResult.Fail(resp.Error ?? "RemoveGitWorktree failed."));
                        return;
                    }

                    tcs.TrySetResult(OperationResult.Ok());
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "RemoveExternalWorktree failed");
                    tcs.TrySetResult(OperationResult.Fail(ex.Message));
                    throw;
                }
            },
            out _);

        if (!started)
            return OperationResult.Fail("A Workspace structural operation is already running.");

        return await tcs.Task.WaitAsync(cancellationToken);
    }

    private sealed class ListWorktreesAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("worktrees")]
        public List<GitWorktreeInfo>? Worktrees { get; set; }
    }
}
