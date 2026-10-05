using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Service-side enforcement of <see cref="FeatureBranchPolicy"/> (I3): a Feature context keeps every
/// repository on its Feature branch, whatever the caller (dialog, REST route, bulk action).
/// </summary>
public interface IFeatureBranchGuard
{
    /// <summary>
    /// Returns null when the action is allowed, otherwise the refusal message. The special Workspace and an
    /// unknown context are always allowed, after a single context lookup and no further query.
    /// </summary>
    Task<string?> CheckAsync(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        FeatureBranchAction action,
        string? target,
        bool isTag,
        CancellationToken cancellationToken = default);
}

public sealed class FeatureBranchGuard(IDbContextFactory<AppDbContext> dbContextFactory) : IFeatureBranchGuard
{
    public async Task<string?> CheckAsync(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        FeatureBranchAction action,
        string? target,
        bool isTag,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var context = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == contextId.Value)
            .Select(c => new
            {
                c.Kind,
                FeatureName = c.WorkspaceFeature != null ? c.WorkspaceFeature.Name : null
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (context is null || context.Kind == WorkspaceFeatureContextKind.Workspace || context.FeatureName is null)
            return null;

        var pinnedTag = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == contextId.Value
                        && r.WorkspaceRepository!.RepositoryId == repositoryId)
            .Select(r => r.PinnedTag)
            .FirstOrDefaultAsync(cancellationToken);

        return FeatureBranchPolicy.Evaluate(
            action,
            FeatureBranchPolicy.ExpectedBranch(context.FeatureName, pinnedTag),
            pinnedTag,
            target,
            isTag);
    }
}
