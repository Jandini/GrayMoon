using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

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

    /// <summary>
    /// Persists branches and tags for a workspace repository. Remote branches are stored as <c>origin/&lt;name&gt;</c> whether the
    /// caller holds the bare name (the Worker lists them bare) or the prefixed ref, so every writer agrees on one row per branch.
    /// Removes branches/tags not in the fetched list, adds new ones, updates LastSeenAt for existing ones. Optionally marks the default branch (e.g. main or master) and the currently checked-out tag.</summary>
    public async Task PersistAsync(
        int workspaceRepositoryId,
        IReadOnlyList<string>? localBranches,
        IReadOnlyList<string>? remoteBranches,
        string? defaultBranchName,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default,
        bool save = true)
    {
        var now = DateTime.UtcNow;
        var existingBranches = await dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId)
            .ToListAsync(cancellationToken);

        var fetchedRefs = new HashSet<(string Name, bool IsRemote, bool IsTag)>();
        // Tracks the worker-provided rank for tags so we can persist "newest first" order; branches default to 0.
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
                    fetchedRefs.Add((ToRemoteBranchName(branch), true, false));
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

        // Update WorkspaceRepositoryLink.CheckedOutTag from the worker-reported value when tags were refreshed.
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

        if (save)
            await SaveBranchChangesAsync(cancellationToken);
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

    /// <summary>The persisted name of a remote branch: always prefixed with <c>origin/</c>, whether the caller holds the bare branch name or the remote ref.</summary>
    public static string ToRemoteBranchName(string branchName)
    {
        var trimmed = branchName.Trim();
        return trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? trimmed : "origin/" + trimmed;
    }

    /// <summary>Adds every missing local branch in one query and one save. Never removes or touches other rows. Returns the number of rows this call inserted. A row inserted by a parallel writer counts as already stored.</summary>
    public async Task<int> AddLocalBranchesAsync(
        IReadOnlyList<(int WorkspaceRepositoryId, string BranchName)> branches,
        CancellationToken cancellationToken = default)
    {
        var wanted = branches
            .Where(b => !string.IsNullOrWhiteSpace(b.BranchName) && !IsSyntheticGitRef(b.BranchName))
            .Select(b => (b.WorkspaceRepositoryId, BranchName: b.BranchName.Trim()))
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
            return 0;

        var linkIds = wanted.Select(b => b.WorkspaceRepositoryId).Distinct().ToList();
        var existing = await dbContext.RepositoryBranches
            .Where(rb => linkIds.Contains(rb.WorkspaceRepositoryId) && !rb.IsRemote && !rb.IsTag)
            .Select(rb => new { rb.WorkspaceRepositoryId, rb.BranchName })
            .ToListAsync(cancellationToken);
        var existingSet = existing.Select(rb => (rb.WorkspaceRepositoryId, rb.BranchName)).ToHashSet();

        var now = DateTime.UtcNow;
        var toAdd = wanted
            .Where(b => !existingSet.Contains((b.WorkspaceRepositoryId, b.BranchName)))
            .Select(b => new RepositoryBranch
            {
                WorkspaceRepositoryId = b.WorkspaceRepositoryId,
                BranchName = b.BranchName,
                IsRemote = false,
                LastSeenAt = now,
                IsDefault = false
            })
            .ToList();
        if (toAdd.Count == 0)
            return 0;

        dbContext.RepositoryBranches.AddRange(toAdd);
        return toAdd.Count - await SaveBranchChangesAsync(cancellationToken);
    }

    /// <summary>Adds a remote branch row (<c>origin/&lt;name&gt;</c>) when it is not persisted yet. Returns true when a row was added.</summary>
    public async Task<bool> AddRemoteBranchAsync(int workspaceRepositoryId, string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName) || IsSyntheticGitRef(branchName))
            return false;

        var remoteBranchName = ToRemoteBranchName(branchName);
        var exists = await dbContext.RepositoryBranches
            .AnyAsync(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId && rb.IsRemote && !rb.IsTag && rb.BranchName == remoteBranchName, cancellationToken);
        if (exists)
            return false;

        dbContext.RepositoryBranches.Add(new RepositoryBranch
        {
            WorkspaceRepositoryId = workspaceRepositoryId,
            BranchName = remoteBranchName,
            IsRemote = true,
            LastSeenAt = DateTime.UtcNow,
            IsDefault = false
        });
        return await SaveBranchChangesAsync(cancellationToken) == 0;
    }

    /// <summary>
    /// Removes the row of a branch that git no longer has. A remote branch is matched by its <c>origin/</c>-prefixed name, so the
    /// bare branch name works for both kinds. Tags are never touched. Returns the number of rows removed.
    /// </summary>
    public async Task<int> RemoveBranchAsync(int workspaceRepositoryId, string branchName, bool isRemote, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return 0;

        var persistedName = isRemote ? ToRemoteBranchName(branchName) : branchName.Trim();
        var rows = await dbContext.RepositoryBranches
            .Where(rb => rb.WorkspaceRepositoryId == workspaceRepositoryId && rb.IsRemote == isRemote && !rb.IsTag && rb.BranchName == persistedName)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return 0;

        dbContext.RepositoryBranches.RemoveRange(rows);
        await dbContext.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Saves pending branch changes. Returns how many added branch rows were already stored by another writer.
    /// A workspace checkout runs every repository at once, and each checkout's hook sync inserts the same local
    /// branch this writer just decided to add. The unique index rejects the later insert. That row is already
    /// stored, so it is dropped and the rest of this save is written. The checkouts stay parallel.
    /// </summary>
    private async Task<int> SaveBranchChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var alreadyStored = await DetachBranchesAlreadyStoredAsync(cancellationToken);
            if (alreadyStored == 0)
                throw;

            logger.LogDebug("Branch row was already stored by a parallel writer; kept the existing row.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return alreadyStored;
        }
    }

    /// <summary>Detaches added branch rows whose unique key is already committed. Returns how many were detached.</summary>
    private async Task<int> DetachBranchesAlreadyStoredAsync(CancellationToken cancellationToken)
    {
        var added = dbContext.ChangeTracker.Entries<RepositoryBranch>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();
        if (added.Count == 0)
            return 0;

        var linkIds = added.Select(branch => branch.WorkspaceRepositoryId).Distinct().ToList();
        var stored = await dbContext.RepositoryBranches
            .AsNoTracking()
            .Where(branch => linkIds.Contains(branch.WorkspaceRepositoryId))
            .Select(branch => new { branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote })
            .ToListAsync(cancellationToken);
        var storedKeys = stored
            .Select(branch => (branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote))
            .ToHashSet();

        var detached = 0;
        foreach (var branch in added)
        {
            if (!storedKeys.Contains((branch.WorkspaceRepositoryId, branch.BranchName, branch.IsRemote)))
                continue;

            dbContext.Entry(branch).State = EntityState.Detached;
            detached++;
        }

        return detached;
    }

    /// <summary>SQLite extended result SQLITE_CONSTRAINT_UNIQUE (2067), or the message EF surfaces when the numeric code is absent.</summary>
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && (sqlite.SqliteExtendedErrorCode == 2067
                    || sqlite.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }
}
