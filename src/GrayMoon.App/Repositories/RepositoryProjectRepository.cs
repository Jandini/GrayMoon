using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Merge persistence of RepositoryProjects by ProjectName: remove non-matching, add new, update existing.</summary>
public sealed class RepositoryProjectRepository(AppDbContext dbContext, ILogger<RepositoryProjectRepository> logger)
{
    /// <summary>Gets all projects for repositories linked to the given workspace.</summary>
    public async Task<List<RepositoryProject>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.LinkedRepositoryId)
            .ToListAsync(cancellationToken);

        if (repoIds.Count == 0)
            return new List<RepositoryProject>();

        return await dbContext.RepositoryProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => repoIds.Contains(p.RepositoryId))
            .OrderBy(p => p.ProjectType == ProjectType.Service ? 0 : p.ProjectType == ProjectType.Library ? 1 : p.ProjectType == ProjectType.Package ? 2 : p.ProjectType == ProjectType.Test ? 3 : 4)
            .ThenBy(p => p.ProjectName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Merges projects for a repository by ProjectName. Removes persisted projects not in <paramref name="projects"/>; adds new; updates existing.</summary>
    public async Task MergeRepositoryProjectsAsync(int repositoryId, IReadOnlyList<SyncProjectInfo> projects, CancellationToken cancellationToken = default)
    {
        var byName = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectName))
            .GroupBy(p => p.ProjectName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var incomingNames = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);

        var existing = await dbContext.RepositoryProjects
            .Where(p => p.RepositoryId == repositoryId)
            .ToListAsync(cancellationToken);

        var toRemove = existing.Where(p => !incomingNames.Contains(p.ProjectName)).ToList();
        if (toRemove.Count > 0)
        {
            dbContext.RepositoryProjects.RemoveRange(toRemove);
            logger.LogDebug("RepositoryProjects merge: RepositoryId={RepositoryId}, removed {Count} by name", repositoryId, toRemove.Count);
        }

        foreach (var p in existing.Where(p => incomingNames.Contains(p.ProjectName)))
        {
            if (byName.TryGetValue(p.ProjectName, out var info))
            {
                p.ProjectType = info.ProjectType;
                p.ProjectFilePath = info.ProjectFilePath;
                p.TargetFramework = info.TargetFramework;
                p.PackageId = string.IsNullOrWhiteSpace(info.PackageId) ? null : info.PackageId;
            }
        }

        var existingNames = new HashSet<string>(existing.Select(e => e.ProjectName), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, info) in byName)
        {
            if (existingNames.Contains(name))
                continue;
            dbContext.RepositoryProjects.Add(new RepositoryProject
            {
                RepositoryId = repositoryId,
                ProjectName = name,
                ProjectType = info.ProjectType,
                ProjectFilePath = info.ProjectFilePath,
                TargetFramework = info.TargetFramework,
                PackageId = string.IsNullOrWhiteSpace(info.PackageId) ? null : info.PackageId
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: RepositoryProjects. Action=Merge, RepositoryId={RepositoryId}, Removed={Removed}, AddedOrUpdated={Count}",
            repositoryId, toRemove.Count, byName.Count);
    }
}
