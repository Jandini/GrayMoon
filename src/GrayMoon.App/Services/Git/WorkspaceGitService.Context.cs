using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>
    /// Resolves worker <c>workspaceRoot</c> + folder name for the given Feature/Workspace context.
    /// Callers must pass an explicit context id - never infer from ambient UI state.
    /// </summary>
    private Task<WorkerWorkspaceArgs> ResolveWorkerPathArgsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
        => _pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);

    /// <summary>
    /// Per-repository Feature parent branch for divergence / PR base.
    /// Empty dictionary for the special Workspace context (worker then uses the repo default).
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string?>> GetDivergenceBaseBranchesByRepositoryIdAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        var info = await _contextResolver.GetRequiredAsync(contextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return new Dictionary<int, string?>();

        var rows = await (
            from r in _dbContext.WorkspaceFeatureRepositories.AsNoTracking()
            join l in _dbContext.WorkspaceRepositories.AsNoTracking()
                on r.WorkspaceRepositoryId equals l.WorkspaceRepositoryId
            where r.WorkspaceFeatureContextId == contextId.Value
            select new { l.RepositoryId, r.ParentBranchName }
        ).ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.RepositoryId, x => (string?)x.ParentBranchName);
    }
}
