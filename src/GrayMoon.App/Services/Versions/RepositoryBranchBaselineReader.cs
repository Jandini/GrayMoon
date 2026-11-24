using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Versions;

/// <summary>Where a branch started: the parent branch (without <c>origin/</c>) and the commit the branch was created at.</summary>
public sealed record BranchBaseline(string ParentBranchName, string BaseCommitSha);

/// <summary>
/// Reads the recorded branch baseline of repositories in one context. A Feature's baseline is its
/// <see cref="WorkspaceFeatureRepository"/> row (none for a tag pin or a read only snapshot, or when the parent branch
/// is unknown); the special Workspace's is the <see cref="WorkspaceRepositoryBranchBaseline"/> row of the branch the
/// repository has checked out. It never reads <c>DefaultBranchName</c> or link state: a branch GrayMoon did not create
/// has no baseline (I5, D6).
/// </summary>
public static class RepositoryBranchBaselineReader
{
    /// <param name="branchByLinkId">The checked-out branch of each repository in this context, keyed by WorkspaceRepositoryId.</param>
    public static async Task<Dictionary<int, BranchBaseline>> LoadAsync(
        AppDbContext db,
        int workspaceFeatureContextId,
        bool isSpecialWorkspace,
        IReadOnlyDictionary<int, string?> branchByLinkId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, BranchBaseline>();
        if (branchByLinkId.Count == 0)
            return result;

        var linkIds = branchByLinkId.Keys.ToList();
        if (!isSpecialWorkspace)
        {
            var rows = await db.WorkspaceFeatureRepositories
                .AsNoTracking()
                .Where(r => r.WorkspaceFeatureContextId == workspaceFeatureContextId && linkIds.Contains(r.WorkspaceRepositoryId))
                .Select(r => new { r.WorkspaceRepositoryId, r.ParentBranchName, r.BaseCommitSha, r.PinnedTag, r.IsReferenceSnapshot })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.PinnedTag != null || row.IsReferenceSnapshot)
                    continue;
                if (string.IsNullOrWhiteSpace(row.ParentBranchName) || string.IsNullOrWhiteSpace(row.BaseCommitSha))
                    continue;
                result[row.WorkspaceRepositoryId] = new BranchBaseline(StripOrigin(row.ParentBranchName), row.BaseCommitSha.Trim());
            }

            return result;
        }

        var baselines = await db.WorkspaceRepositoryBranchBaselines
            .AsNoTracking()
            .Where(b => b.WorkspaceFeatureContextId == workspaceFeatureContextId && linkIds.Contains(b.WorkspaceRepositoryId))
            .Select(b => new { b.WorkspaceRepositoryId, b.BranchName, b.ParentBranchName, b.BaseCommitSha })
            .ToListAsync(cancellationToken);
        foreach (var row in baselines)
        {
            if (!branchByLinkId.TryGetValue(row.WorkspaceRepositoryId, out var branch)
                || string.IsNullOrWhiteSpace(branch)
                || !string.Equals(branch, row.BranchName, StringComparison.Ordinal))
                continue;
            result[row.WorkspaceRepositoryId] = new BranchBaseline(StripOrigin(row.ParentBranchName), row.BaseCommitSha.Trim());
        }

        return result;
    }

    /// <summary>
    /// The branch each read only snapshot of a Feature was taken from, keyed by WorkspaceRepositoryId: the
    /// <see cref="WorkspaceFeatureRepository.SourceBranchName"/> of rows with <see cref="WorkspaceFeatureRepository.IsReferenceSnapshot"/>.
    /// A snapshot taken from a tag or from a detached Source has no source branch and is not returned. This is not a
    /// branch baseline: a snapshot has no branch of its own, so <see cref="LoadAsync"/> never returns it.
    /// </summary>
    public static async Task<Dictionary<int, string>> LoadReferenceSnapshotSourceBranchesAsync(
        AppDbContext db,
        int workspaceFeatureContextId,
        IReadOnlyCollection<int> linkIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        if (linkIds.Count == 0)
            return result;

        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == workspaceFeatureContextId
                        && linkIds.Contains(r.WorkspaceRepositoryId)
                        && r.IsReferenceSnapshot
                        && r.PinnedTag == null
                        && r.SourceBranchName != null)
            .Select(r => new { r.WorkspaceRepositoryId, r.SourceBranchName })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.SourceBranchName))
                result[row.WorkspaceRepositoryId] = StripOrigin(row.SourceBranchName);
        }

        return result;
    }

    private static string StripOrigin(string branch)
    {
        var trimmed = branch.Trim();
        return trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? trimmed["origin/".Length..] : trimmed;
    }
}
