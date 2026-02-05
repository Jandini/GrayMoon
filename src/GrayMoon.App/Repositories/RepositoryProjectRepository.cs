using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Merge persistence of RepositoryProjects by ProjectName: remove non-matching, add new, update existing.</summary>
public sealed class RepositoryProjectRepository(AppDbContext dbContext, ILogger<RepositoryProjectRepository> logger)
{
    /// <summary>Gets projects that have a PackageId (NuGet packages) for repositories linked to the given workspace.</summary>
    public async Task<List<RepositoryProject>> GetPackagesByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.LinkedRepositoryId)
            .ToListAsync(cancellationToken);

        if (repoIds.Count == 0)
            return new List<RepositoryProject>();

        return await dbContext.RepositoryProjects
            .AsNoTracking()
            .Where(p => repoIds.Contains(p.RepositoryId) && p.PackageId != null && p.PackageId != "")
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.TargetFramework)
            .ToListAsync(cancellationToken);
    }

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

    /// <summary>Replaces project dependencies for workspace projects from sync results. Only dependencies where the referenced package is a workspace project are persisted.</summary>
    public async Task MergeWorkspaceProjectDependenciesAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
        CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.LinkedRepositoryId)
            .ToListAsync(cancellationToken);
        if (repoIds.Count == 0) return;

        var workspaceProjects = await dbContext.RepositoryProjects
            .AsNoTracking()
            .Where(p => repoIds.Contains(p.RepositoryId))
            .ToListAsync(cancellationToken);

        var packageNameToProjectId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in workspaceProjects)
        {
            var key = !string.IsNullOrWhiteSpace(p.PackageId) ? p.PackageId!.Trim() : p.ProjectName.Trim();
            if (!string.IsNullOrEmpty(key) && !packageNameToProjectId.ContainsKey(key))
                packageNameToProjectId[key] = p.ProjectId;
        }

        var dependentProjectIds = new HashSet<int>();
        var edges = new List<(int DependentProjectId, int ReferencedProjectId)>();

        foreach (var (repoId, projectsDetail) in syncResults)
        {
            if (projectsDetail == null || projectsDetail.Count == 0) continue;

            var repoProjects = workspaceProjects.Where(p => p.RepositoryId == repoId).ToDictionary(p => p.ProjectName.Trim(), p => p.ProjectId, StringComparer.OrdinalIgnoreCase);

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
                    edges.Add((dependentProjectId, referencedProjectId));
                }
            }
        }

        if (dependentProjectIds.Count == 0) return;

        var existing = await dbContext.ProjectDependencies
            .Where(d => dependentProjectIds.Contains(d.DependentProjectId))
            .ToListAsync(cancellationToken);
        dbContext.ProjectDependencies.RemoveRange(existing);

        var uniqueEdges = edges.Distinct().ToHashSet();
        foreach (var (depId, refId) in uniqueEdges)
        {
            dbContext.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = depId,
                ReferencedProjectId = refId
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: ProjectDependencies. WorkspaceId={WorkspaceId}, DependentCount={Count}, EdgeCount={Edges}",
            workspaceId, dependentProjectIds.Count, uniqueEdges.Count);
    }

    /// <summary>Returns dependency edges (DependentProjectId, ReferencedProjectId) for the workspace. Suitable for Cytoscape: nodes = projects, edges = this list.</summary>
    public async Task<List<(int DependentProjectId, int ReferencedProjectId)>> GetDependencyEdgesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var repoIds = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => wr.LinkedRepositoryId)
            .ToListAsync(cancellationToken);
        if (repoIds.Count == 0) return new List<(int, int)>();

        var workspaceProjectIds = await dbContext.RepositoryProjects
            .Where(p => repoIds.Contains(p.RepositoryId))
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
    public async Task<List<RepositoryProject>> GetBuildOrderAsync(int workspaceId, CancellationToken cancellationToken = default)
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
            return new List<RepositoryProject>();

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

    /// <summary>Returns repositories in build order with sequence (same sequence = build in parallel) and dependency count per repo.</summary>
    public async Task<List<RepositoryBuildOrderRow>> GetRepositoryBuildOrderAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (projects.Count == 0) return new List<RepositoryBuildOrderRow>();

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
        foreach (var (depId, _) in edges)
        {
            if (!byProject.TryGetValue(depId, out var p)) continue;
            depCountByRepo[p.RepositoryId]++;
        }

        var repoSequence = projects
            .GroupBy(p => p.RepositoryId)
            .Select(g => (
                RepositoryId: g.Key,
                Sequence: g.Max(p => levelByProject.GetValueOrDefault(p.ProjectId, currentLevel)),
                RepositoryName: g.First().Repository?.RepositoryName ?? ""
            ))
            .Where(t => !string.IsNullOrEmpty(t.RepositoryName))
            .ToList();

        return repoSequence
            .Select(t => new RepositoryBuildOrderRow(t.Sequence, t.RepositoryName, depCountByRepo.GetValueOrDefault(t.RepositoryId, 0)))
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
