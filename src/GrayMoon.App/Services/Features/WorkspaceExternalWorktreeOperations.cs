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

            var disk = await InspectWorktreeDiskStatusAsync(mainPath, worktreePath, cancellationToken);
            // Unknown disk state keeps today's non-forced behaviour (Git itself refuses a dirty
            // removal without --force); only an explicit force request is blocked for Unknown.
            var exists = disk.StatusUnknown ? match != null : (disk.Exists || match != null);
            var dirty = !disk.StatusUnknown && disk.IsDirty == true;
            return new ExternalWorktreeCleanupPlan
            {
                Success = true,
                RepositoryName = link.Repository.RepositoryName,
                BranchName = match?.BranchName,
                WorktreePath = worktreePath,
                HeadCommit = match?.HeadSha,
                WorktreeExists = exists,
                IsDirty = dirty,
                WorktreeStatusUnknown = disk.StatusUnknown,
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
                return OperationResult.Fail(FeatureOwnedError);

            // Stray / uninspectable worktrees: allow an explicitly authorized force remove.
            if (!options.AllowForceRemoveDirty)
                return OperationResult.Fail(plan.Error ?? "Analyze failed.");
        }
        else if (plan.WorktreeStatusUnknown && options.AllowForceRemoveDirty)
        {
            // Cannot confirm this worktree is actually dirty, so an explicit force request is
            // refused rather than blindly discarding work. Non-forced removal below is unaffected.
            return OperationResult.Fail("Update the Worker to use this.");
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
            out var operation);

        if (!started)
            return OperationResult.Fail("A Workspace structural operation is already running.");

        var removed = await tcs.Task.WaitAsync(cancellationToken);
        await operation.WhenCompleted;
        return removed;
    }

    /// <summary>
    /// Disk facts for one external worktree, from the Agent's InspectWorktree command. The App never
    /// reads repository or worktree paths from local disk directly (it can run in Docker, where those
    /// paths do not exist). Any failure to reach the Agent, an old Worker, or a parse failure is
    /// Unknown, never treated as Missing or clean.
    /// </summary>
    private async Task<WorktreeDiskStatus> InspectWorktreeDiskStatusAsync(
        string? mainRepositoryPath,
        string? worktreePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath)
            || string.IsNullOrWhiteSpace(worktreePath)
            || !agentBridge.IsAgentConnected)
        {
            return WorktreeDiskStatus.Unknown("Could not check this repository. Make sure the Worker is running, then try again.");
        }

        try
        {
            var response = await agentBridge.SendCommandAsync(
                AgentHubMethods.InspectWorktree,
                new { mainRepositoryPath, worktreePath },
                cancellationToken);
            if (!response.Success)
            {
                var reason = IsUnknownCommandError(response.Error)
                    ? "Update the Worker to use this."
                    : "Could not check this repository. Make sure the Worker is running, then try again.";
                return WorktreeDiskStatus.Unknown(reason);
            }

            var payload = AgentResponseJson.DeserializeAgentResponse<InspectWorktreeAgentResponse>(response.Data);
            if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
                return WorktreeDiskStatus.Unknown("Could not check this repository. Make sure the Worker is running, then try again.");

            return WorktreeDiskStatus.Known(payload.Exists, payload.IsDirty, payload.HasUpstream, payload.AheadOfUpstream, payload.AheadOfDefault);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "InspectWorktree failed for {Path}", worktreePath);
            return WorktreeDiskStatus.Unknown("Could not check this repository. Make sure the Worker is running, then try again.");
        }
    }

    private static bool IsUnknownCommandError(string? error) =>
        !string.IsNullOrWhiteSpace(error) && error.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);

    private sealed class ListWorktreesAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("worktrees")]
        public List<GitWorktreeInfo>? Worktrees { get; set; }
    }
}
