using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class WorkspaceRepository(AppDbContext dbContext, WorkspaceService workspaceService, ILogger<WorkspaceRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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
            .FirstOrDefaultAsync(workspace => workspace.WorkspaceId == workspaceId);
    }

    public async Task<Workspace> AddAsync(string name, IReadOnlyCollection<int> repositoryIds)
    {
        var normalized = NormalizeName(name);
        if (await NameExistsAsync(normalized))
        {
            throw new InvalidOperationException("Workspace name already exists.");
        }

        var workspace = new Workspace { Name = normalized };
        workspace.RootPath = await _workspaceService.GetRootPathAsync();
        _dbContext.Workspaces.Add(workspace);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Add, WorkspaceId={WorkspaceId}, Name={Name}", workspace.WorkspaceId, workspace.Name);

        await _workspaceService.CreateDirectoryAsync(workspace.Name, workspace.RootPath);

        await ReplaceRepositoriesAsync(workspace.WorkspaceId, repositoryIds);
        return workspace;
    }

    public async Task UpdateAsync(int workspaceId, string name, IReadOnlyCollection<int> repositoryIds)
    {
        var normalized = NormalizeName(name);
        if (await NameExistsAsync(normalized, workspaceId))
        {
            throw new InvalidOperationException("Workspace name already exists.");
        }

        var workspace = await _dbContext.Workspaces
            .Include(item => item.Repositories)
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
        {
            throw new InvalidOperationException("Workspace not found.");
        }

        workspace.Name = normalized;
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Persistence: saved Workspace. Action=Update, WorkspaceId={WorkspaceId}, Name={Name}", workspaceId, workspace.Name);

        await ReplaceRepositoriesAsync(workspace.WorkspaceId, repositoryIds);
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

    private async Task ReplaceRepositoriesAsync(int workspaceId, IReadOnlyCollection<int> repositoryIds)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var existing = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync();

        var existingRepoIds = existing.Select(wr => wr.RepositoryId).ToHashSet();
        var newRepoIds = repositoryIds.Distinct().ToHashSet();

        var toRemove = existing.Where(wr => !newRepoIds.Contains(wr.RepositoryId)).ToList();
        var toAdd = newRepoIds.Except(existingRepoIds).ToList();

        var removedRepoIds = toRemove.Select(wr => wr.RepositoryId).ToHashSet();
        if (removedRepoIds.Count > 0)
        {
            var projectsToRemove = await _dbContext.WorkspaceProjects
                .Where(p => p.WorkspaceId == workspaceId && removedRepoIds.Contains(p.RepositoryId))
                .ToListAsync();
            _dbContext.WorkspaceProjects.RemoveRange(projectsToRemove);
            _logger.LogDebug("Persistence: removed {Count} WorkspaceProjects for repos no longer in workspace. WorkspaceId={WorkspaceId}, RepositoryIds=[{RepositoryIds}]",
                projectsToRemove.Count, workspaceId, string.Join(", ", removedRepoIds));
        }

        foreach (var wr in toRemove)
        {
            _dbContext.WorkspaceRepositories.Remove(wr);
        }

        foreach (var repositoryId in toAdd)
        {
            _dbContext.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                SyncStatus = RepoSyncStatus.NeedsSync
            });
        }

        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        _logger.LogInformation("Persistence: saved WorkspaceRepository links. Action=ReplaceRepositories, WorkspaceId={WorkspaceId}, Removed={RemovedCount}, Added={AddedCount}, RepositoryIds=[{RepositoryIds}]",
            workspaceId, toRemove.Count, toAdd.Count, string.Join(", ", newRepoIds));
    }

    private async Task<bool> NameExistsAsync(string name, int? ignoreId = null)
    {
        return await _dbContext.Workspaces.AnyAsync(workspace =>
            workspace.WorkspaceId != ignoreId &&
            workspace.Name.ToLower() == name.ToLower());
    }

    private static string NormalizeName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
    }
}
