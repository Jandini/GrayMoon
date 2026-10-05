using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class WorkspaceRepository(
    AppDbContext dbContext,
    IDbContextFactory<AppDbContext> dbContextFactory,
    WorkspaceService workspaceService,
    IWorkspaceGitChangesNotifier gitChangesNotifier,
    ILogger<WorkspaceRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly IWorkspaceGitChangesNotifier _gitChangesNotifier = gitChangesNotifier ?? throw new ArgumentNullException(nameof(gitChangesNotifier));
    private readonly ILogger<WorkspaceRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<List<Workspace>> GetAllAsync()
    {
        return await _dbContext.Workspaces
            .AsNoTracking()
            .Include(workspace => workspace.Repositories)
            .OrderBy(workspace => workspace.Name)
            .ToListAsync();
    }

    public async Task<Workspace?> GetByIdAsync(int workspaceId)
    {
        return await _dbContext.Workspaces
            .AsNoTracking()
            .Include(workspace => workspace.Repositories)
            .ThenInclude(link => link.Repository)
            .ThenInclude(repository => repository!.Connector)
            .Include(workspace => workspace.Repositories)
            .ThenInclude(link => link.PullRequest)
            .FirstOrDefaultAsync(workspace => workspace.WorkspaceId == workspaceId);
    }

    /// <summary>Loads workspace metadata without repository links (for incremental list pages).</summary>
    public async Task<Workspace?> GetHeaderAsync(int workspaceId)
    {
        return await _dbContext.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(workspace => workspace.WorkspaceId == workspaceId);
    }

    public async Task<Workspace> AddAsync(string name, IReadOnlyCollection<int> repositoryIds)
    {
        var normalized = NormalizeName(name);

        // Fresh context: the injected AppDbContext is circuit-scoped and may still track
        // Workspace / WRL graphs loaded by Git Changes or other page services.
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        if (await NameExistsAsync(db, normalized))
        {
            throw new InvalidOperationException("Workspace name already exists.");
        }

        var workspace = new Workspace { Name = normalized };
        workspace.RootPath = await _workspaceService.GetRootPathAsync();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Add, WorkspaceId={WorkspaceId}, Name={Name}", workspace.WorkspaceId, workspace.Name);

        await _workspaceService.CreateDirectoryAsync(workspace.Name, workspace.RootPath);

        await ReplaceRepositoriesAsync(db, workspace.WorkspaceId, repositoryIds);
        return workspace;
    }

    public async Task UpdateAsync(int workspaceId, string name, IReadOnlyCollection<int> repositoryIds, string? rootPath)
    {
        var normalized = NormalizeName(name);
        var normalizedRootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath.Trim();

        // Fresh context: the injected AppDbContext is circuit-scoped and may still track
        // Workspace / WRL graphs loaded by Git Changes or other page services.
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        if (await NameExistsAsync(db, normalized, workspaceId))
        {
            throw new InvalidOperationException("Workspace name already exists.");
        }

        var workspace = await db.Workspaces
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
        {
            throw new InvalidOperationException("Workspace not found.");
        }

        var nameChanged = !string.Equals(workspace.Name, normalized, StringComparison.Ordinal);
        var rootPathChanged = !string.Equals(workspace.RootPath, normalizedRootPath, StringComparison.Ordinal);

        // A rename or root change would move the primary checkout, and every Feature worktree's
        // Git link points into that checkout's .git\worktrees, so it is refused up front, before
        // any write, whenever the Workspace has Features. An unchanged name and root path is never
        // blocked here, even when Features exist.
        if (nameChanged || rootPathChanged)
        {
            if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId))
            {
                throw new InvalidOperationException(
                    "Rename and root changes are not possible while Features exist. Remove Features first.");
            }
        }

        // One transaction for the rename/root save and the membership change, so a membership
        // failure (Features exist and the repository set actually changed) rolls back the rename
        // instead of leaving a partial save.
        await using var transaction = await db.Database.BeginTransactionAsync();

        workspace.Name = normalized;
        workspace.RootPath = normalizedRootPath;
        await db.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Update, WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);

        await ReplaceRepositoriesCoreAsync(db, workspace.WorkspaceId, repositoryIds);

        await transaction.CommitAsync();
    }

    public const string WorkspaceDeleteBlockedByFeaturesMessage = "Remove the Features first, then delete the Workspace.";

    public async Task DeleteAsync(int workspaceId)
    {
        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
        {
            return;
        }

        // Deleting the Workspace would drop its Feature rows while their worktrees, branches and
        // features\<name> folders stay on disk with nothing left in GrayMoon to clean them.
        if (await _dbContext.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId))
        {
            throw new InvalidOperationException(WorkspaceDeleteBlockedByFeaturesMessage);
        }

        _dbContext.Workspaces.Remove(workspace);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Delete, WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);

        // Prune the now-deleted workspace's per-workspace check-coalescing gate so it doesn't linger for the
        // life of the process.
        WorkspaceFileVersionService.RemoveWorkspaceCheckLock(workspaceId);
    }

    public async Task UpdateSyncMetadataAsync(int workspaceId, DateTime lastSyncedAt, bool isInSync)
    {
        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId);

        if (workspace != null)
        {
            workspace.LastSyncedAt = lastSyncedAt;
            workspace.IsInSync = isInSync;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved Workspace sync metadata. Action=UpdateSyncMetadata, WorkspaceId={WorkspaceId}, LastSyncedAt={LastSyncedAt:O}, IsInSync={IsInSync}", workspaceId, lastSyncedAt, isInSync);
        }
    }

    public async Task UpdateIsInSyncAsync(int workspaceId, bool isInSync)
    {
        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId);

        if (workspace != null)
        {
            workspace.IsInSync = isInSync;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved Workspace. Action=UpdateIsInSync, WorkspaceId={WorkspaceId}, IsInSync={IsInSync}", workspaceId, isInSync);
        }
    }

    public async Task UpdateExcludeAiWorkflowsAsync(int workspaceId, bool excludeAiWorkflows)
    {
        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId);

        if (workspace != null)
        {
            workspace.ExcludeAiWorkflows = excludeAiWorkflows;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Persistence: saved Workspace. Action=UpdateExcludeAiWorkflows, WorkspaceId={WorkspaceId}, ExcludeAiWorkflows={ExcludeAiWorkflows}", workspaceId, excludeAiWorkflows);
        }
    }

    public async Task<Workspace?> GetDefaultAsync()
    {
        return await _dbContext.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(workspace => workspace.IsDefault);
    }

    public async Task ToggleDefaultAsync(int workspaceId)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
        {
            return;
        }

        if (workspace.IsDefault)
        {
            workspace.IsDefault = false;
            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            _logger.LogInformation("Persistence: saved Workspace. Action=ToggleDefault (cleared), WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);
            return;
        }

        var currentDefaults = await _dbContext.Workspaces
            .Where(item => item.IsDefault && item.WorkspaceId != workspaceId)
            .ToListAsync();

        foreach (var existing in currentDefaults)
        {
            existing.IsDefault = false;
        }

        workspace.IsDefault = true;
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=ToggleDefault (set), WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);
    }

    /// <summary>
    /// Lightweight check (no dependency-package lookup) for which of the given repositories have unpushed commits
    /// or a branch never pushed upstream, scoped to <paramref name="workspaceFeatureContextId"/>: reads
    /// <see cref="WorkspaceRepositoryContextState"/>'s own OutgoingCommits/BranchHasUpstream/CheckedOutTag when a
    /// row exists for that context, falling back to the shared <see cref="WorkspaceRepositoryLink"/> only for the
    /// special Workspace context (or when no context-state row has been persisted for that repo yet) - same rule
    /// as <c>WorkspaceProjectRepository.GetContextVersionAndLevelByRepoAsync</c>. A repo with neither a context
    /// row nor special-Workspace status is treated as "not needing push" rather than borrowing the Workspace's
    /// answer - see AGENTS.md "Feature-context scoping".
    /// </summary>
    public async Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        int workspaceFeatureContextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
            return new HashSet<int>();

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var isSpecialWorkspace = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && repositoryIds.Contains(wr.RepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, wr.CheckedOutTag, wr.OutgoingCommits, wr.BranchHasUpstream })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return new HashSet<int>();

        var linkIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var states = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId && linkIds.Contains(s.WorkspaceRepositoryId))
            .Select(s => new { s.WorkspaceRepositoryId, s.CheckedOutTag, s.OutgoingCommits, s.BranchHasUpstream })
            .ToListAsync(cancellationToken);
        var stateByLinkId = states.ToDictionary(s => s.WorkspaceRepositoryId);

        var result = new HashSet<int>();
        foreach (var link in links)
        {
            string? checkedOutTag;
            int? outgoingCommits;
            bool? branchHasUpstream;

            if (stateByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                checkedOutTag = state.CheckedOutTag;
                outgoingCommits = state.OutgoingCommits;
                branchHasUpstream = state.BranchHasUpstream;
            }
            else if (isSpecialWorkspace)
            {
                checkedOutTag = link.CheckedOutTag;
                outgoingCommits = link.OutgoingCommits;
                branchHasUpstream = link.BranchHasUpstream;
            }
            else
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(checkedOutTag))
                continue;
            if ((outgoingCommits ?? 0) > 0 || branchHasUpstream == false)
                result.Add(link.RepositoryId);
        }
        return result;
    }

    /// <summary>
    /// Repo ids that "need a push" scoped to <paramref name="workspaceFeatureContextId"/>, used only to decide
    /// whether the Workspace Action notification panel should show the synchronized-push modal (not which repos
    /// actually get pushed). Same fallback rule as <see cref="GetRepositoryIdsNeedingPushAsync"/>: a repo's
    /// effective CheckedOutTag/OutgoingCommits/BranchHasUpstream/DependencyLevel come from its own
    /// <see cref="WorkspaceRepositoryContextState"/> when a row exists for that context, falling back to the
    /// shared <see cref="WorkspaceRepositoryLink"/> only for the special Workspace context (or when no
    /// context-state row has been persisted for that repo yet - a Feature with no row is "not needing push", never
    /// the Workspace's answer). Keeps the notification panel's two historical predicates unchanged so the special
    /// Workspace's result never changes: <paramref name="includeNeverPushedUpstream"/> false matches the main Push
    /// button (outgoing commits only); true matches the per-repo push badge (outgoing commits or never pushed
    /// upstream). <paramref name="maxLevel"/> filters to repos at or below that dependency level (main Push button
    /// only; pass null to include every level).
    /// </summary>
    public async Task<IReadOnlySet<int>> GetRepositoryIdsThatNeedPushForNotificationAsync(
        int workspaceId,
        int workspaceFeatureContextId,
        IReadOnlySet<int> repositoryIds,
        int? maxLevel,
        bool includeNeverPushedUpstream,
        CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
            return new HashSet<int>();

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var isSpecialWorkspace = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && repositoryIds.Contains(wr.RepositoryId))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, wr.CheckedOutTag, wr.OutgoingCommits, wr.BranchHasUpstream, wr.DependencyLevel })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return new HashSet<int>();

        var linkIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var states = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId && linkIds.Contains(s.WorkspaceRepositoryId))
            .Select(s => new { s.WorkspaceRepositoryId, s.CheckedOutTag, s.OutgoingCommits, s.BranchHasUpstream, s.DependencyLevel })
            .ToListAsync(cancellationToken);
        var stateByLinkId = states.ToDictionary(s => s.WorkspaceRepositoryId);

        var result = new HashSet<int>();
        foreach (var link in links)
        {
            string? checkedOutTag;
            int? outgoingCommits;
            bool? branchHasUpstream;
            int? dependencyLevel;

            if (stateByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                checkedOutTag = state.CheckedOutTag;
                outgoingCommits = state.OutgoingCommits;
                branchHasUpstream = state.BranchHasUpstream;
                dependencyLevel = state.DependencyLevel;
            }
            else if (isSpecialWorkspace)
            {
                checkedOutTag = link.CheckedOutTag;
                outgoingCommits = link.OutgoingCommits;
                branchHasUpstream = link.BranchHasUpstream;
                dependencyLevel = link.DependencyLevel;
            }
            else
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(checkedOutTag))
                continue;
            if (maxLevel.HasValue && (dependencyLevel ?? 0) > maxLevel.Value)
                continue;

            var needsPush = (outgoingCommits ?? 0) > 0 || (includeNeverPushedUpstream && branchHasUpstream == false);
            if (needsPush)
                result.Add(link.RepositoryId);
        }
        return result;
    }

    public async Task AddRepositoriesAsync(int workspaceId, IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Cannot add Workspace repositories while Features exist. Remove Features first.");
        }

        var workspace = await _dbContext.Workspaces
            .Include(w => w.Repositories)
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken);

        if (workspace == null)
            throw new InvalidOperationException("Workspace not found.");

        var existingRepoIds = workspace.Repositories.Select(wr => wr.RepositoryId).ToHashSet();
        var toAdd = repositoryIds.Distinct().Where(id => !existingRepoIds.Contains(id)).ToList();
        if (toAdd.Count == 0)
            return;

        foreach (var repositoryId in toAdd)
        {
            _dbContext.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                SyncStatus = RepoSyncStatus.NeedsSync
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Persistence: saved WorkspaceRepository links. Action=AddRepositories, WorkspaceId={WorkspaceId}, Added={AddedCount}, RepositoryIds=[{RepositoryIds}]",
            workspaceId, toAdd.Count, string.Join(", ", toAdd));
    }

    private async Task ReplaceRepositoriesAsync(AppDbContext db, int workspaceId, IReadOnlyCollection<int> repositoryIds)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await ReplaceRepositoriesCoreAsync(db, workspaceId, repositoryIds);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Does the actual repository-membership swap. Never opens its own transaction, so it can run
    /// either on its own (via <see cref="ReplaceRepositoriesAsync"/>) or inside a caller's existing
    /// transaction (<see cref="UpdateAsync"/>, so a membership failure rolls back a rename too).
    /// </summary>
    private async Task ReplaceRepositoriesCoreAsync(AppDbContext db, int workspaceId, IReadOnlyCollection<int> repositoryIds)
    {
        var current = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync();

        var requestedIds = repositoryIds.Distinct().ToHashSet();

        var validRepoIds = await db.Repositories
            .AsNoTracking()
            .Where(r => requestedIds.Contains(r.RepositoryId))
            .Select(r => r.RepositoryId)
            .ToListAsync();
        var validSet = validRepoIds.ToHashSet();

        var invalidIds = requestedIds.Except(validSet).ToList();
        if (invalidIds.Count > 0)
        {
            _logger.LogWarning(
                "ReplaceRepositories: WorkspaceId={WorkspaceId}, {InvalidCount} repository ID(s) requested by UI no longer exist in the database and will be skipped. StaleIds=[{StaleIds}]",
                workspaceId, invalidIds.Count, string.Join(", ", invalidIds));
        }

        var existingRepoIds = current.Select(wr => wr.RepositoryId).ToHashSet();

        // An unchanged membership set is a no-op: a save that does not actually add or remove any
        // repository must never be blocked by the Features-exist guard below.
        if (existingRepoIds.SetEquals(validSet))
            return;

        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId))
        {
            throw new InvalidOperationException(
                "Cannot change Workspace repository membership while Features exist. Remove Features first.");
        }

        var toRemove = current.Where(wr => !validSet.Contains(wr.RepositoryId)).ToList();
        var toAdd = validSet.Except(existingRepoIds).ToList();

        _logger.LogDebug(
            "ReplaceRepositories: WorkspaceId={WorkspaceId}, CurrentLinks={CurrentLinks}, ToRemove={ToRemoveCount} [{ToRemoveIds}], ToAdd={ToAddCount} [{ToAddIds}]",
            workspaceId, current.Count, toRemove.Count, string.Join(", ", toRemove.Select(wr => wr.RepositoryId)),
            toAdd.Count, string.Join(", ", toAdd));

        if (toRemove.Count > 0)
        {
            var removedRepoIds = toRemove.Select(wr => wr.RepositoryId).ToList();
            var wrlIdsToRemove = toRemove.Select(wr => wr.WorkspaceRepositoryId).ToList();

            await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(
                db, wrlIdsToRemove, removedRepoIds, workspaceId);

            _logger.LogDebug(
                "ReplaceRepositories: Removing WorkspaceRepositoryLink rows. WorkspaceId={WorkspaceId}, WrlIds=[{WrlIds}]",
                workspaceId, string.Join(", ", wrlIdsToRemove));

            await db.WorkspaceRepositories
                .Where(wr => wrlIdsToRemove.Contains(wr.WorkspaceRepositoryId))
                .ExecuteDeleteAsync();
        }

        foreach (var repositoryId in toAdd)
        {
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                SyncStatus = RepoSyncStatus.NeedsSync
            });
        }

        if (toAdd.Count > 0)
            await db.SaveChangesAsync();

        _logger.LogInformation(
            "Persistence: saved WorkspaceRepository links. Action=ReplaceRepositories, WorkspaceId={WorkspaceId}, Removed={RemovedCount}, Added={AddedCount}, RepositoryIds=[{RepositoryIds}]",
            workspaceId, toRemove.Count, toAdd.Count, string.Join(", ", validSet));

        if (toRemove.Count > 0)
            _gitChangesNotifier.Publish(workspaceId, IWorkspaceGitChangesNotifier.AllContexts);
    }

    private async Task<bool> NameExistsAsync(string name, int? ignoreId = null) =>
        await NameExistsAsync(_dbContext, name, ignoreId);

    private static async Task<bool> NameExistsAsync(AppDbContext db, string name, int? ignoreId = null)
    {
        return await db.Workspaces.AnyAsync(workspace =>
            workspace.WorkspaceId != ignoreId &&
            workspace.Name.ToLower() == name.ToLower());
    }

    private static string NormalizeName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
    }
}
