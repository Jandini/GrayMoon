using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class WorkspaceFileVersionConfigRepository(
    AppDbContext dbContext,
    ILogger<WorkspaceFileVersionConfigRepository> logger)
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspaceFileVersionConfigRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<WorkspaceFileVersionConfig?> GetByFileIdAsync(int fileId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.WorkspaceFileVersionConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.FileId == fileId, cancellationToken);
    }

    /// <summary>Returns all version configs for files belonging to the given workspace, including their File and Repository.</summary>
    public async Task<IReadOnlyList<WorkspaceFileVersionConfig>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.WorkspaceFileVersionConfigs
            .AsNoTracking()
            .Include(c => c.File)
                .ThenInclude(f => f!.Repository)
            .Where(c => c.File!.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(int fileId, string pattern, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.WorkspaceFileVersionConfigs
            .FirstOrDefaultAsync(c => c.FileId == fileId, cancellationToken);

        if (existing == null)
        {
            _dbContext.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig
            {
                FileId = fileId,
                VersionPattern = pattern
            });
        }
        else
        {
            existing.VersionPattern = pattern;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Upserted version config for FileId={FileId}", fileId);
    }

    public async Task DeleteByFileIdAsync(int fileId, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.WorkspaceFileVersionConfigs
            .FirstOrDefaultAsync(c => c.FileId == fileId, cancellationToken);

        if (existing != null)
        {
            _dbContext.WorkspaceFileVersionConfigs.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted version config for FileId={FileId}", fileId);
        }
    }
}
