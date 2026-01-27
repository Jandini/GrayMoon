using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class WorkspaceRepository
{
    private readonly AppDbContext _dbContext;

    public WorkspaceRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

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
            .ThenInclude(link => link.GitHubRepository)
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
        _dbContext.Workspaces.Add(workspace);
        await _dbContext.SaveChangesAsync();

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
    }

    private async Task ReplaceRepositoriesAsync(int workspaceId, IReadOnlyCollection<int> repositoryIds)
    {
        var existing = _dbContext.WorkspaceRepositoryLinks
            .Where(link => link.WorkspaceId == workspaceId);

        _dbContext.WorkspaceRepositoryLinks.RemoveRange(existing);
        await _dbContext.SaveChangesAsync();

        if (repositoryIds.Count == 0)
        {
            return;
        }

        var links = repositoryIds
            .Distinct()
            .Select(repositoryId => new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                GitHubRepositoryId = repositoryId
            })
            .ToList();

        await _dbContext.WorkspaceRepositoryLinks.AddRangeAsync(links);
        await _dbContext.SaveChangesAsync();
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
