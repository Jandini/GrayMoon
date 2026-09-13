using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Merges projects for a repository in a workspace by ProjectName. Removes persisted projects not in <paramref name="projects"/>; adds new; updates existing.</summary>
    public async Task MergeWorkspaceProjectsAsync(int workspaceId, int repositoryId, IReadOnlyList<SyncProjectInfo> projects, CancellationToken cancellationToken = default)
    {
        // Generated (virtual/inferred) package rows are owned by SyncGeneratedPackageDependenciesAsync, not by
        // this per-repo sync reconciliation - a real repo's own project scan must never delete a generated
        // package row it happens to "produce" (it has no physical .csproj producing it, so it never appears here).
        var existing = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && p.RepositoryId == repositoryId && !p.IsGenerated)
            .ToListAsync(cancellationToken);

        var (removed, addedOrUpdated) = MergeProjectsForRepository(workspaceId, repositoryId, projects, existing);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: WorkspaceProjects. Action=Merge, WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}, Removed={Removed}, AddedOrUpdated={Count}",
            workspaceId, repositoryId, removed, addedOrUpdated);
    }

    /// <summary>
    /// Batched form of <see cref="MergeWorkspaceProjectsAsync"/> for multiple repos in one workspace: loads every
    /// repo's existing (non-generated) <see cref="WorkspaceProject"/> rows with a single query, merges each repo's
    /// projects in memory, and calls <see cref="AppDbContext.SaveChangesAsync"/> exactly once for the whole batch -
    /// instead of one query plus one save per repo in a caller-side loop.
    /// </summary>
    public async Task MergeWorkspaceProjectsBatchAsync(
        int workspaceId,
        IReadOnlyList<(int RepositoryId, IReadOnlyList<SyncProjectInfo> Projects)> repoProjects,
        CancellationToken cancellationToken = default)
    {
        if (repoProjects == null || repoProjects.Count == 0) return;

        var repoIds = repoProjects.Select(r => r.RepositoryId).ToHashSet();
        var existingAll = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && repoIds.Contains(p.RepositoryId) && !p.IsGenerated)
            .ToListAsync(cancellationToken);
        var existingByRepo = existingAll.ToLookup(p => p.RepositoryId);

        var totalRemoved = 0;
        var totalAddedOrUpdated = 0;
        foreach (var (repositoryId, projects) in repoProjects)
        {
            var (removed, addedOrUpdated) = MergeProjectsForRepository(workspaceId, repositoryId, projects, existingByRepo[repositoryId].ToList());
            totalRemoved += removed;
            totalAddedOrUpdated += addedOrUpdated;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: WorkspaceProjects. Action=MergeBatch, WorkspaceId={WorkspaceId}, RepoCount={RepoCount}, Removed={Removed}, AddedOrUpdated={Count}",
            workspaceId, repoProjects.Count, totalRemoved, totalAddedOrUpdated);
    }

    /// <summary>Merges one repo's incoming projects against its already-loaded existing rows: stages Remove/Add on <c>dbContext</c> and mutates tracked entities in place. Caller saves. Returns (Removed, AddedOrUpdated) for logging.</summary>
    private (int Removed, int AddedOrUpdated) MergeProjectsForRepository(
        int workspaceId,
        int repositoryId,
        IReadOnlyList<SyncProjectInfo> projects,
        IReadOnlyList<WorkspaceProject> existing)
    {
        var byName = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectName))
            .GroupBy(p => p.ProjectName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var incomingNames = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);

        var toRemove = existing.Where(p => !incomingNames.Contains(p.ProjectName)).ToList();
        if (toRemove.Count > 0)
        {
            dbContext.WorkspaceProjects.RemoveRange(toRemove);
            logger.LogDebug("WorkspaceProjects merge: WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}, removed {Count} by name", workspaceId, repositoryId, toRemove.Count);
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
            dbContext.WorkspaceProjects.Add(new WorkspaceProject
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                ProjectName = name,
                ProjectType = info.ProjectType,
                ProjectFilePath = info.ProjectFilePath,
                TargetFramework = info.TargetFramework,
                PackageId = string.IsNullOrWhiteSpace(info.PackageId) ? null : info.PackageId
            });
        }

        return (toRemove.Count, byName.Count);
    }

    /// <summary>Replaces project dependencies for workspace projects from sync results. Only dependencies where the referenced package is a workspace project are persisted. When <paramref name="persistDependencyLevel"/> is true, levels are recomputed from the full DB graph (not the partial merge batch). When false, callers such as <c>WorkspaceGitService.PersistVersionsAsync</c> follow with <c>RecomputeAndPersistRepositoryDependencyStatsAsync</c>.</summary>
    public async Task MergeWorkspaceProjectDependenciesAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
    {
        var workspaceProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (workspaceProjects.Count == 0) return;

        var packageNameToProjectId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in workspaceProjects)
        {
            var key = !string.IsNullOrWhiteSpace(p.PackageId) ? p.PackageId!.Trim() : p.ProjectName.Trim();
            if (!string.IsNullOrEmpty(key) && !packageNameToProjectId.ContainsKey(key))
                packageNameToProjectId[key] = p.ProjectId;
        }

        var dependentProjectIds = new HashSet<int>();
        var edges = new List<(int DependentProjectId, int ReferencedProjectId, string? Version)>();

        foreach (var (repoId, projectsDetail) in syncResults)
        {
            if (projectsDetail == null || projectsDetail.Count == 0) continue;

            var repoProjects = workspaceProjects.Where(p => p.WorkspaceId == workspaceId && p.RepositoryId == repoId).ToDictionary(p => p.ProjectName.Trim(), p => p.ProjectId, StringComparer.OrdinalIgnoreCase);

            foreach (var info in projectsDetail)
            {
                if (string.IsNullOrWhiteSpace(info.ProjectName)) continue;
                if (!repoProjects.TryGetValue(info.ProjectName.Trim(), out var dependentProjectId)) continue;

                dependentProjectIds.Add(dependentProjectId);

                if (info.PackageReferences.Count == 0) continue;
                foreach (var pr in info.PackageReferences)
                {
                    if (string.IsNullOrWhiteSpace(pr.Name)) continue;
                    if (!packageNameToProjectId.TryGetValue(pr.Name.Trim(), out var referencedProjectId)) continue;
                    if (referencedProjectId == dependentProjectId) continue;
                    var version = string.IsNullOrWhiteSpace(pr.Version) ? null : pr.Version.Trim();
                    edges.Add((dependentProjectId, referencedProjectId, version));
                }
            }
        }

        if (dependentProjectIds.Count == 0) return;

        var existing = await dbContext.ProjectDependencies
            .Where(d => dependentProjectIds.Contains(d.DependentProjectId))
            .ToListAsync(cancellationToken);
        dbContext.ProjectDependencies.RemoveRange(existing);

        var uniqueEdges = edges
            .GroupBy(e => (e.DependentProjectId, e.ReferencedProjectId))
            .Select(g => (g.Key.DependentProjectId, g.Key.ReferencedProjectId, g.First().Version))
            .ToList();
        foreach (var (depId, refId, version) in uniqueEdges)
        {
            dbContext.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = depId,
                ReferencedProjectId = refId,
                Version = version
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: ProjectDependencies. WorkspaceId={WorkspaceId}, DependentCount={Count}, EdgeCount={Edges}",
            workspaceId, dependentProjectIds.Count, uniqueEdges.Count);

        if (persistDependencyLevel)
            await RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);
    }

    /// <summary>Persists the new Version for ProjectDependencies that were updated by sync dependencies. Matches by (RepoId, ProjectPath) -> DependentProjectId and PackageId -> ReferencedProjectId.</summary>
    public async Task UpdateProjectDependencyVersionsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string ProjectPath, string PackageId, string NewVersion)> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates == null || updates.Count == 0) return;

        var projects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0) return;

        var dependentKeyToProjectId = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectFilePath))
            .GroupBy(p => (p.RepositoryId, ProjectPath: p.ProjectFilePath.Trim().ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First().ProjectId);

        var packageNameToProjectId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            var key = !string.IsNullOrWhiteSpace(p.PackageId) ? p.PackageId!.Trim() : p.ProjectName.Trim();
            if (!string.IsNullOrEmpty(key) && !packageNameToProjectId.ContainsKey(key))
                packageNameToProjectId[key] = p.ProjectId;
        }

        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var depRows = await dbContext.ProjectDependencies
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .ToListAsync(cancellationToken);
        var byDepRef = depRows.ToLookup(d => (d.DependentProjectId, d.ReferencedProjectId));

        var updated = 0;
        foreach (var (repoId, projectPath, packageId, newVersion) in updates)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(newVersion))
                continue;

            var pathNorm = projectPath.Trim();
            var depKey = (repoId, pathNorm.ToLowerInvariant());
            if (!dependentKeyToProjectId.TryGetValue(depKey, out var dependentProjectId))
                continue;
            if (!packageNameToProjectId.TryGetValue(packageId.Trim(), out var referencedProjectId))
                continue;

            var key = (dependentProjectId, referencedProjectId);
            foreach (var row in byDepRef[key])
            {
                if (row.Version != newVersion)
                {
                    row.Version = newVersion;
                    updated++;
                }
            }
        }

        if (updated > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug("UpdateProjectDependencyVersions: WorkspaceId={WorkspaceId}, Updated={Count}", workspaceId, updated);
    }
}
