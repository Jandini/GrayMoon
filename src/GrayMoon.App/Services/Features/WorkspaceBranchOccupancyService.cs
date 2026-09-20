using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Agent;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Classifies Switch Branch rows using <c>git worktree list --porcelain</c> plus GrayMoon Feature ownership.
/// Does not auto-adopt external worktrees.
/// </summary>
public sealed class WorkspaceBranchOccupancyService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    IAgentBridge agentBridge) : IWorkspaceBranchOccupancyService
{
    public async Task<IReadOnlyDictionary<string, BranchOccupancyBadge>> GetBadgesForRepositoryAsync(
        int workspaceId,
        int workspaceRepositoryId,
        WorkspaceFeatureContextId viewingContextId,
        CancellationToken cancellationToken = default)
    {
        var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var mainPath = await pathResolver.GetRepositoryPathAsync(special, workspaceRepositoryId, cancellationToken);
        var currentPath = await pathResolver.GetRepositoryPathAsync(viewingContextId, workspaceRepositoryId, cancellationToken);

        var listResp = await agentBridge.SendCommandAsync(
            AgentHubMethods.ListGitWorktrees,
            new { mainRepositoryPath = mainPath },
            cancellationToken);

        IReadOnlyList<GitWorktreeInfo> worktrees = [];
        if (listResp.Success && listResp.Data != null)
        {
            var payload = AgentResponseJson.DeserializeAgentResponse<ListWorktreesAgentResponse>(listResp.Data);
            worktrees = payload?.Worktrees ?? [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var featureRows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Include(r => r.WorkspaceFeatureContext)!.ThenInclude(c => c!.WorkspaceFeature)
            .Where(r => r.WorkspaceRepositoryId == workspaceRepositoryId)
            .ToListAsync(cancellationToken);

        var byPath = featureRows
            .Where(r => !string.IsNullOrWhiteSpace(r.WorktreePath))
            .GroupBy(r => r.WorktreePath!.Replace('/', '\\').TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, BranchOccupancyBadge>(StringComparer.OrdinalIgnoreCase);
        foreach (var wt in worktrees)
        {
            if (wt.IsBare || wt.IsDetached || string.IsNullOrWhiteSpace(wt.BranchName))
                continue;

            var kind = GitWorktreeOccupancy.ClassifyBranch(worktrees, wt.BranchName, currentPath);
            var badge = BranchOccupancyKind.None;
            string? featureName = null;
            string? worktreePath = wt.WorktreePath;

            if (kind == GitWorktreeBranchOccupancyKind.Current)
            {
                badge = BranchOccupancyKind.Current;
            }
            else if (kind == GitWorktreeBranchOccupancyKind.OccupiedElsewhere)
            {
                var pathKey = (wt.WorktreePath ?? "").Replace('/', '\\').TrimEnd('\\');
                if (byPath.TryGetValue(pathKey, out var owned))
                {
                    badge = BranchOccupancyKind.Feature;
                    featureName = owned.WorkspaceFeatureContext?.WorkspaceFeature?.Name;
                }
                else
                {
                    badge = BranchOccupancyKind.Worktree;
                }
            }

            if (badge != BranchOccupancyKind.None)
            {
                result[wt.BranchName] = new BranchOccupancyBadge
                {
                    Kind = badge,
                    FeatureName = featureName,
                    WorktreePath = worktreePath,
                    AllowCheckout = badge == BranchOccupancyKind.None || badge == BranchOccupancyKind.Current,
                    AllowOrdinaryDelete = badge == BranchOccupancyKind.None,
                    RequiresFeatureCleanup = badge == BranchOccupancyKind.Feature,
                    RequiresExternalCleanup = badge == BranchOccupancyKind.Worktree
                };
            }
        }

        // Feature branches owned in DB but not currently listed still block Workspace checkout.
        foreach (var row in featureRows)
        {
            var name = row.WorkspaceFeatureContext?.WorkspaceFeature?.Name;
            if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name))
                continue;
            result[name] = new BranchOccupancyBadge
            {
                Kind = BranchOccupancyKind.Feature,
                FeatureName = name,
                WorktreePath = row.WorktreePath,
                AllowCheckout = false,
                AllowOrdinaryDelete = false,
                RequiresFeatureCleanup = true,
                RequiresExternalCleanup = false
            };
        }

        return result;
    }
}

public interface IWorkspaceBranchOccupancyService
{
    Task<IReadOnlyDictionary<string, BranchOccupancyBadge>> GetBadgesForRepositoryAsync(
        int workspaceId,
        int workspaceRepositoryId,
        WorkspaceFeatureContextId viewingContextId,
        CancellationToken cancellationToken = default);
}

public enum BranchOccupancyKind
{
    None = 0,
    Current = 1,
    Feature = 2,
    Worktree = 3
}

public sealed class BranchOccupancyBadge
{
    public required BranchOccupancyKind Kind { get; init; }
    public string? FeatureName { get; init; }
    public string? WorktreePath { get; init; }
    public bool AllowCheckout { get; init; }
    public bool AllowOrdinaryDelete { get; init; }
    public bool RequiresFeatureCleanup { get; init; }
    public bool RequiresExternalCleanup { get; init; }
}

file sealed class ListWorktreesAgentResponse
{
    public bool Success { get; set; }
    public List<GitWorktreeInfo>? Worktrees { get; set; }
}
