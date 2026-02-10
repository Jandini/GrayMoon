using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Merge persistence of WorkspaceProjects by ProjectName: remove non-matching, add new, update existing.</summary>
public sealed class WorkspaceProjectRepository(AppDbContext dbContext, ILogger<WorkspaceProjectRepository> logger)
{
    /// <summary>Gets projects that have a PackageId (NuGet packages) for repositories linked to the given workspace.</summary>
    public async Task<List<WorkspaceProject>> GetPackagesByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.PackageId != null && p.PackageId != "")
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.TargetFramework)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Gets all projects for repositories linked to the given workspace.</summary>
    public async Task<List<WorkspaceProject>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.ProjectType == ProjectType.Service ? 0 : p.ProjectType == ProjectType.Library ? 1 : p.ProjectType == ProjectType.Package ? 2 : p.ProjectType == ProjectType.Test ? 3 : 4)
            .ThenBy(p => p.ProjectName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Merges projects for a repository in a workspace by ProjectName. Removes persisted projects not in <paramref name="projects"/>; adds new; updates existing.</summary>
    public async Task MergeWorkspaceProjectsAsync(int workspaceId, int repositoryId, IReadOnlyList<SyncProjectInfo> projects, CancellationToken cancellationToken = default)
    {
        var byName = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectName))
            .GroupBy(p => p.ProjectName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var incomingNames = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);

        var existing = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && p.RepositoryId == repositoryId)
            .ToListAsync(cancellationToken);

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

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: WorkspaceProjects. Action=Merge, WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}, Removed={Removed}, AddedOrUpdated={Count}",
            workspaceId, repositoryId, toRemove.Count, byName.Count);
    }

    /// <summary>Replaces project dependencies for workspace projects from sync results. Only dependencies where the referenced package is a workspace project are persisted.</summary>
    public async Task MergeWorkspaceProjectDependenciesAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
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
    }

    /// <summary>Returns payload for syncing dependency versions: per repo, list of (project path, package ID to new version) for dependencies that do not match the referenced repo's GitVersion.</summary>
    public async Task<List<SyncDependenciesRepoPayload>> GetSyncDependenciesPayloadAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var versionByRepoId = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.GitVersion })
            .ToListAsync(cancellationToken);
        var versionByRepo = versionByRepoId.ToDictionary(x => x.RepositoryId, x => x.GitVersion, null);

        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0) return new List<SyncDependenciesRepoPayload>();
        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var byProject = projects.ToDictionary(p => p.ProjectId);

        var dependencies = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
            .ToListAsync(cancellationToken);

        var repoIds = projects.Select(p => p.RepositoryId).Distinct().ToList();
        var repoToProjectUpdates = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>(repoIds.Count);
        foreach (var repoId in repoIds)
            repoToProjectUpdates[repoId] = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in dependencies)
        {
            if (!byProject.TryGetValue(d.DependentProjectId, out var depProj) || !byProject.TryGetValue(d.ReferencedProjectId, out var refProj))
                continue;

            var refVersion = versionByRepo.GetValueOrDefault(refProj.RepositoryId);
            var depVersion = d.Version?.Trim() ?? "";
            var refVersionNorm = refVersion?.Trim() ?? "";
            if (depVersion == refVersionNorm || string.IsNullOrEmpty(refVersionNorm))
                continue;

            var packageId = !string.IsNullOrWhiteSpace(refProj.PackageId) ? refProj.PackageId!.Trim() : refProj.ProjectName.Trim();
            if (string.IsNullOrEmpty(packageId))
                continue;

            var projectPath = depProj.ProjectFilePath?.Trim() ?? "";
            if (string.IsNullOrEmpty(projectPath))
                continue;

            var repoUpdates = repoToProjectUpdates[depProj.RepositoryId];
            if (!repoUpdates.TryGetValue(projectPath, out var packageDict))
            {
                packageDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                repoUpdates[projectPath] = packageDict;
            }
            packageDict[packageId] = refVersionNorm;
        }

        var result = new List<SyncDependenciesRepoPayload>();
        foreach (var p in projects.GroupBy(p => p.RepositoryId).Select(g => g.First()))
        {
            var repoId = p.RepositoryId;
            var repoName = p.Repository?.RepositoryName ?? "";
            if (string.IsNullOrEmpty(repoName))
                continue;
            if (!repoToProjectUpdates.TryGetValue(repoId, out var projectUpdatesDict) || projectUpdatesDict.Count == 0)
                continue;

            var projectUpdates = projectUpdatesDict
                .Select(kv => new SyncDependenciesProjectUpdate(kv.Key, kv.Value.Select(p => (p.Key, p.Value)).ToList()))
                .ToList();
            result.Add(new SyncDependenciesRepoPayload(repoId, repoName, projectUpdates));
        }

        return result.OrderBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>Returns dependency edges (DependentProjectId, ReferencedProjectId) for the workspace. Suitable for Cytoscape: nodes = projects, edges = this list.</summary>
    public async Task<List<(int DependentProjectId, int ReferencedProjectId)>> GetDependencyEdgesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspaceProjectIds = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId)
            .Select(p => p.ProjectId)
            .ToListAsync(cancellationToken);
        if (workspaceProjectIds.Count == 0) return new List<(int, int)>();

        var idSet = workspaceProjectIds.ToHashSet();
        var rows = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => idSet.Contains(d.DependentProjectId) && idSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.DependentProjectId, r.ReferencedProjectId)).ToList();
    }

    /// <summary>Returns workspace projects in build order (dependencies first). Uses topological sort; returns empty list if cycle detected.</summary>
    public async Task<List<WorkspaceProject>> GetBuildOrderAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (projects.Count == 0) return projects;

        var edges = await GetDependencyEdgesAsync(workspaceId, cancellationToken);
        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var byProject = projects.ToDictionary(p => p.ProjectId);

        var inDegree = projects.ToDictionary(p => p.ProjectId, _ => 0);
        var revEdges = projects.ToDictionary(p => p.ProjectId, _ => new List<int>());
        foreach (var (depId, refId) in edges)
        {
            if (!projectIds.Contains(depId) || !projectIds.Contains(refId)) continue;
            inDegree[depId]++;
            revEdges[refId].Add(depId);
        }

        var queue = new Queue<int>(projects.Where(p => inDegree[p.ProjectId] == 0).Select(p => p.ProjectId));
        var order = new List<int>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            order.Add(n);
            foreach (var depId in revEdges[n])
            {
                inDegree[depId]--;
                if (inDegree[depId] == 0)
                    queue.Enqueue(depId);
            }
        }

        if (order.Count != projects.Count)
            return new List<WorkspaceProject>();

        return order.Select(id => byProject[id]).ToList();
    }

    /// <summary>Returns the dependency graph for the workspace: nodes (projects with labels) and edges. Suitable for Cytoscape (nodes + edges).</summary>
    public async Task<ProjectDependencyGraph> GetDependencyGraphAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var edges = await GetDependencyEdgesAsync(workspaceId, cancellationToken);

        var nodes = projects.Select(p => new ProjectDependencyNode(
            p.ProjectId,
            p.PackageId ?? p.ProjectName,
            p.PackageId,
            p.ProjectName,
            p.Repository?.RepositoryName ?? "")).ToList();

        var edgeList = edges.Select(e => new ProjectDependencyEdge(e.DependentProjectId, e.ReferencedProjectId)).ToList();

        return new ProjectDependencyGraph(nodes, edgeList);
    }

    /// <summary>Returns repository-level dependency graph (nodes = repos, edges = repo depends on repo). For Cytoscape.</summary>
    public async Task<RepositoryDependencyGraph> GetRepositoryDependencyGraphAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (projects.Count == 0) return new RepositoryDependencyGraph(new List<RepositoryDependencyNode>(), new List<RepositoryDependencyEdge>());

        var edges = await GetDependencyEdgesAsync(workspaceId, cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);

        var repoNodes = projects
            .GroupBy(p => p.RepositoryId)
            .Select(g => new RepositoryDependencyNode(g.Key, g.First().Repository?.RepositoryName ?? "Repo " + g.Key))
            .Where(n => !string.IsNullOrEmpty(n.RepositoryName))
            .ToList();

        var repoEdges = new HashSet<(int Dep, int Ref)>();
        foreach (var (depProjectId, refProjectId) in edges)
        {
            if (!byProject.TryGetValue(depProjectId, out var depProj) || !byProject.TryGetValue(refProjectId, out var refProj)) continue;
            if (depProj.RepositoryId == refProj.RepositoryId) continue;
            repoEdges.Add((depProj.RepositoryId, refProj.RepositoryId));
        }

        var edgeList = repoEdges.Select(e => new RepositoryDependencyEdge(e.Dep, e.Ref)).ToList();
        return new RepositoryDependencyGraph(repoNodes, edgeList);
    }

    /// <summary>Returns repositories in build order with sequence (same sequence = build in parallel), version from persistence, and dependency count per repo.</summary>
    public async Task<List<RepositoryBuildOrderRow>> GetRepositoryBuildOrderAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (projects.Count == 0) return new List<RepositoryBuildOrderRow>();

        var versionByRepoId = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.GitVersion })
            .ToListAsync(cancellationToken);
        var versionByRepo = versionByRepoId.ToDictionary(x => x.RepositoryId, x => x.GitVersion, null);

        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var byProject = projects.ToDictionary(p => p.ProjectId);

        var dependencyRows = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
            .ToListAsync(cancellationToken);

        var edges = dependencyRows.Select(d => (d.DependentProjectId, d.ReferencedProjectId)).Distinct().ToList();

        var inDegree = projects.ToDictionary(p => p.ProjectId, _ => 0);
        var revEdges = projects.ToDictionary(p => p.ProjectId, _ => new List<int>());
        foreach (var (depId, refId) in edges)
        {
            if (!projectIds.Contains(depId) || !projectIds.Contains(refId)) continue;
            inDegree[depId]++;
            revEdges[refId].Add(depId);
        }

        var levelByProject = new Dictionary<int, int>();
        var queue = new Queue<int>(projects.Where(p => inDegree[p.ProjectId] == 0).Select(p => p.ProjectId));
        var currentLevel = 1;
        var remaining = projects.Count;
        while (queue.Count > 0)
        {
            var levelSize = queue.Count;
            for (var i = 0; i < levelSize; i++)
            {
                var n = queue.Dequeue();
                levelByProject[n] = currentLevel;
                remaining--;
                foreach (var depId in revEdges[n])
                {
                    inDegree[depId]--;
                    if (inDegree[depId] == 0)
                        queue.Enqueue(depId);
                }
            }
            currentLevel++;
        }

        if (remaining != 0)
            return new List<RepositoryBuildOrderRow>();

        var depCountByRepo = projects.GroupBy(p => p.RepositoryId).ToDictionary(g => g.Key, _ => 0);
        var unmatchedCountByRepo = projects.GroupBy(p => p.RepositoryId).ToDictionary(g => g.Key, _ => 0);

        foreach (var d in dependencyRows)
        {
            if (!byProject.TryGetValue(d.DependentProjectId, out var depProj)) continue;
            if (!byProject.TryGetValue(d.ReferencedProjectId, out var refProj)) continue;
            var depRepoId = depProj.RepositoryId;
            depCountByRepo[depRepoId] = depCountByRepo.GetValueOrDefault(depRepoId, 0) + 1;

            var refRepoVersion = versionByRepo.GetValueOrDefault(refProj.RepositoryId);
            var depVersion = d.Version?.Trim() ?? "";
            var refVersion = refRepoVersion?.Trim() ?? "";
            if (depVersion != refVersion)
                unmatchedCountByRepo[depRepoId] = unmatchedCountByRepo.GetValueOrDefault(depRepoId, 0) + 1;
        }

        var repoIds = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.RepositoryId)
            .ToListAsync(cancellationToken);

        var repoSequence = projects
            .GroupBy(p => p.RepositoryId)
            .Where(g => repoIds.Contains(g.Key))
            .Select(g => (
                RepositoryId: g.Key,
                Sequence: g.Max(p => levelByProject.GetValueOrDefault(p.ProjectId, currentLevel)),
                RepositoryName: g.First().Repository?.RepositoryName ?? ""
            ))
            .Where(t => !string.IsNullOrEmpty(t.RepositoryName))
            .ToList();

        return repoSequence
            .Select(t => new RepositoryBuildOrderRow(
                t.Sequence,
                t.RepositoryName,
                versionByRepo.GetValueOrDefault(t.RepositoryId),
                depCountByRepo.GetValueOrDefault(t.RepositoryId, 0),
                unmatchedCountByRepo.GetValueOrDefault(t.RepositoryId, 0)))
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
