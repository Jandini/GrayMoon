using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class WorkspaceFileRepository(AppDbContext dbContext, ILogger<WorkspaceFileRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspaceFileRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IReadOnlyList<WorkspaceFile>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.WorkspaceFiles
            .AsNoTracking()
            .Include(f => f.Repository)
            .Where(f => f.WorkspaceId == workspaceId)
            .OrderBy(f => f.Repository!.RepositoryName)
            .ThenBy(f => f.FilePath)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRangeAsync(int workspaceId, IReadOnlyList<(int RepositoryId, string FileName, string FilePath)> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return;

        var existing = await _dbContext.WorkspaceFiles
            .Where(f => f.WorkspaceId == workspaceId)
            .Select(f => new { f.RepositoryId, f.FilePath })
            .ToListAsync(cancellationToken);
        var existingSet = existing.Select(x => (x.RepositoryId, x.FilePath)).ToHashSet();

        var toAdd = items
            .Where(x => !existingSet.Contains((x.RepositoryId, x.FilePath)))
            .Distinct()
            .ToList();

        foreach (var (repositoryId, fileName, filePath) in toAdd)
        {
            _dbContext.WorkspaceFiles.Add(new WorkspaceFile
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                FileName = fileName,
                FilePath = filePath
            });
        }

        if (toAdd.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Added {Count} workspace files for WorkspaceId={WorkspaceId}", toAdd.Count, workspaceId);
        }
    }

    public async Task RemoveAsync(int fileId, CancellationToken cancellationToken = default)
    {
        var file = await _dbContext.WorkspaceFiles.FindAsync([fileId], cancellationToken);
        if (file != null)
        {
            _dbContext.WorkspaceFiles.Remove(file);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Removed workspace file FileId={FileId}", fileId);
        }
    }
}
