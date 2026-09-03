using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class WorkspaceRepository(
    AppDbContext dbContext,
    IDbContextFactory<AppDbContext> dbContextFactory,
    WorkspaceService workspaceService,
    ILogger<WorkspaceRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
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

        workspace.Name = normalized;
        workspace.RootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath.Trim();
        await db.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Update, WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);

        await ReplaceRepositoriesAsync(db, workspace.WorkspaceId, repositoryIds);
    }

    public async Task DeleteAsync(int workspaceId)
    {
        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
        {
            return;
        }

        _dbContext.Workspaces.Remove(workspace);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Delete, WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);
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

    public async Task AddRepositoriesAsync(int workspaceId, IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken = default)
    {
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

        await transaction.CommitAsync();
        _logger.LogInformation(
            "Persistence: saved WorkspaceRepository links. Action=ReplaceRepositories, WorkspaceId={WorkspaceId}, Removed={RemovedCount}, Added={AddedCount}, RepositoryIds=[{RepositoryIds}]",
            workspaceId, toRemove.Count, toAdd.Count, string.Join(", ", validSet));
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
