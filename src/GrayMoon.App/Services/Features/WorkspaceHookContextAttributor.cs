using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Resolves hook/sync repository paths to a WorkspaceFeatureContextId.
/// Never defaults an unmatched path to the special Workspace context.
/// </summary>
public sealed class WorkspaceHookContextAttributor(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<WorkspaceHookContextAttributor> logger) : IWorkspaceHookContextAttributor
{
    public async Task<WorkspaceFeatureContextId?> ResolveAsync(
        int workspaceId,
        int repositoryId,
        string? repositoryPath,
        int? claimedContextId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            // Legacy agents/hooks without a path write the special Workspace context only.
            var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
            if (claimedContextId is int claimed && claimed != special.Value)
            {
                logger.LogWarning(
                    "Hook attribution: claimed context {ClaimedContextId} ignored for legacy null path; using special Workspace {ContextId}",
                    claimed, special.Value);
            }
            return special;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .FirstOrDefaultAsync(
                l => l.WorkspaceId == workspaceId && l.RepositoryId == repositoryId,
                cancellationToken);
        if (link is null)
        {
            logger.LogWarning(
                "Hook attribution: workspace {WorkspaceId} repository {RepositoryId} link not found",
                workspaceId, repositoryId);
            return null;
        }

        var normalizedIncoming = NormalizePath(repositoryPath);
        var matches = new List<WorkspaceFeatureContextId>();

        var specialId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var specialPath = await pathResolver.GetRepositoryPathAsync(specialId, link.WorkspaceRepositoryId, cancellationToken);
        if (PathsEqual(normalizedIncoming, NormalizePath(specialPath)))
            matches.Add(specialId);

        var featureRepos = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceRepositoryId == link.WorkspaceRepositoryId)
            .Select(r => new { r.WorkspaceFeatureContextId, r.WorktreePath })
            .ToListAsync(cancellationToken);

        foreach (var row in featureRepos)
        {
            if (string.IsNullOrWhiteSpace(row.WorktreePath))
                continue;
            if (PathsEqual(normalizedIncoming, NormalizePath(row.WorktreePath)))
                matches.Add(new WorkspaceFeatureContextId(row.WorkspaceFeatureContextId));
        }

        var distinct = matches.Distinct().ToList();
        if (distinct.Count == 0)
        {
            logger.LogWarning(
                "Hook attribution: unknown path {RepositoryPath} for workspace {WorkspaceId} repository {RepositoryId}; skipping state write",
                repositoryPath, workspaceId, repositoryId);
            return null;
        }

        if (distinct.Count > 1)
        {
            logger.LogWarning(
                "Hook attribution: ambiguous path {RepositoryPath} matched {Count} contexts; skipping state write",
                repositoryPath, distinct.Count);
            return null;
        }

        var resolved = distinct[0];
        if (claimedContextId is int claim && claim != resolved.Value)
        {
            logger.LogWarning(
                "Hook attribution: agent claimed context {ClaimedContextId} but path resolved to {ResolvedContextId}; using path",
                claim, resolved.Value);
        }

        return resolved;
    }

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\').Trim();

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
