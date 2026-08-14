using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>
/// Owns the <c>RepositoryBranches</c> rows for a workspace repository. Split out of
/// <see cref="WorkspaceGitService"/> so <see cref="WorkspaceRepositoryStateWriter"/> can persist
/// branches without taking a dependency on the service that calls it.
/// </summary>
public sealed class RepositoryBranchWriter(AppDbContext dbContext, ILogger<RepositoryBranchWriter> logger)
{
    /// <summary>True when a ref name is one of git's synthetic placeholders that appear in detached HEAD state (e.g. <c>(HEAD detached at v1.0)</c>, <c>(no branch)</c>, <c>origin/(no branch)</c>). These are not real branches and must never be persisted.</summary>
    private static bool IsSyntheticGitRef(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return true;
        if (trimmed.StartsWith("(", StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
            return true;
        if (trimmed.EndsWith("/(no branch)", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>Persists branches and tags for a workspace repository. Removes branches/tags not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master) and the currently checked-out tag.</summary>
    public async Task PersistAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existingBranches = await dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId)
            .ToListAsync(cancellationToken);

        var fetchedRefs = new HashSet<(string Name, bool IsRemote, bool IsTag)>();
        // Tracks the agent-provided rank for tags so we can persist "newest first" order; branches default to 0.
        var sortIndexByRef = new Dictionary<(string Name, bool IsRemote, bool IsTag), int>();
        if (localBranches != null)
        {
            foreach (var branch in localBranches)
            {
                if (!string.IsNullOrWhiteSpace(branch) && !IsSyntheticGitRef(branch))
                    fetchedRefs.Add((branch, false, false));
            }
        }
        if (remoteBranches != null)
        {
            foreach (var branch in remoteBranches)
            {
                if (!string.IsNullOrWhiteSpace(branch) && !IsSyntheticGitRef(branch))
                    fetchedRefs.Add((branch, true, false));
            }
        }
        if (tags != null)
        {
            var rank = 0;
            foreach (var tag in tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    var key = (tag, false, true);
                    if (fetchedRefs.Add(key))
                        sortIndexByRef[key] = rank++;
                }
            }
        }

        // Clear IsDefault for all existing; we will set it for the default branch below
        foreach (var b in existingBranches)
            b.IsDefault = false;

        // Update existing rows or add new ones
        foreach (var (name, isRemote, isTag) in fetchedRefs)
        {
            var isDefault = !isTag && !string.IsNullOrWhiteSpace(defaultBranchName) && string.Equals(name, defaultBranchName, StringComparison.OrdinalIgnoreCase);
            var sortIndex = sortIndexByRef.TryGetValue((name, isRemote, isTag), out var rank) ? rank : 0;
            var existing = existingBranches.FirstOrDefault(b => b.BranchName == name && b.IsRemote == isRemote && b.IsTag == isTag);
            if (existing != null)
            {
                existing.LastSeenAt = now;
                existing.IsDefault = isDefault;
                if (isTag)
                    existing.SortIndex = sortIndex;
            }
            else
            {
                dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = workspaceRepositoryId,
                    BranchName = name,
                    IsRemote = isRemote,
                    IsTag = isTag,
                    LastSeenAt = now,
                    IsDefault = isDefault,
                    SortIndex = isTag ? sortIndex : 0
                });
            }
        }

        // Remove rows that were not fetched (no longer exist). Tags are removed only when a tag list was
        // provided so callers that pass only branches (e.g. legacy paths) do not wipe persisted tags.
        var toRemove = existingBranches
            .Where(b => !fetchedRefs.Contains((b.BranchName, b.IsRemote, b.IsTag)))
            .Where(b => !b.IsTag || tags != null)
            .Where(b => b.IsTag || (localBranches != null || remoteBranches != null))
            .ToList();
        if (toRemove.Count > 0)
        {
            dbContext.RepositoryBranches.RemoveRange(toRemove);
        }

        // Update WorkspaceRepositoryLink.CheckedOutTag from the agent-reported value when tags were refreshed.
        if (tags != null)
        {
            var link = await dbContext.WorkspaceRepositories
                .FirstOrDefaultAsync(wr => wr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
            if (link != null)
            {
                if (!string.IsNullOrWhiteSpace(currentTag))
                {
                    link.CheckedOutTag = currentTag;
                    link.BranchName = null;
                    link.BranchHasUpstream = null;
                    link.OutgoingCommits = null;
                    link.IncomingCommits = null;
                    link.DefaultBranchBehindCommits = null;
                    link.DefaultBranchAheadCommits = null;
                    // Determine if a newer tag exists: SortIndex 0 = newest. If currentTag is not at index 0, there is a newer tag.
                    var tagIdx = -1;
                    for (var i = 0; i < tags.Count; i++)
                    {
                        if (string.Equals(tags[i], currentTag, StringComparison.OrdinalIgnoreCase))
                        { tagIdx = i; break; }
                    }
                    link.HasNewerTag = tagIdx > 0;
                }
                else if (!string.IsNullOrWhiteSpace(link.CheckedOutTag))
                {
                    // Tag list refreshed but we are no longer on a tag; clear the pinned state.
                    link.CheckedOutTag = null;
                    link.HasNewerTag = null;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Persisted {Count} ref(s) for workspace repository {WorkspaceRepositoryId}", fetchedRefs.Count, workspaceRepositoryId);
    }

    /// <summary>Prunes remote branch rows that are no longer present in git after a fetch --prune. Returns the number of rows removed.</summary>
    public async Task<int> PruneRemoteBranchesAsync(int workspaceRepositoryId, IReadOnlyList<string> freshRemoteBranches, CancellationToken cancellationToken = default)
    {
        var freshRemotes = freshRemoteBranches
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? b : "origin/" + b)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dbRemotes = await dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId && rb.IsRemote)
            .ToListAsync(cancellationToken);

        var stale = dbRemotes.Where(rb => !freshRemotes.Contains(rb.BranchName ?? "")).ToList();
        if (stale.Count == 0)
            return 0;

        dbContext.RepositoryBranches.RemoveRange(stale);
        await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}
