using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Recomputes DependencyLevel, Dependencies, and UnmatchedDeps from current DB state (workspace projects, project dependencies, and file version configs) and persists to WorkspaceRepositoryLink. Use after a repository version change (e.g. notify job) so the grid can refresh without re-reading .csproj files.</summary>
    public async Task RecomputeAndPersistRepositoryDependencyStatsAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspaceProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var uniqueEdges = new List<(int DependentProjectId, int ReferencedProjectId, string? Version)>();
        if (workspaceProjects.Count > 0)
        {
            var projectIds = workspaceProjects.Select(p => p.ProjectId).ToHashSet();
            var depRows = await dbContext.ProjectDependencies
                .AsNoTracking()
                .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
                .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
                .ToListAsync(cancellationToken);
            uniqueEdges = depRows
                .GroupBy(d => (d.DependentProjectId, d.ReferencedProjectId))
                .Select(g => (g.Key.DependentProjectId, g.Key.ReferencedProjectId, g.First().Version))
                .ToList();
        }

        await PersistRepositoryDependencyLevelAndDependenciesAsync(workspaceId, workspaceProjects, uniqueEdges, cancellationToken);
    }

    /// <summary>Computes dependency level and dependency count per repo and persists them on WorkspaceRepositoryLink. Merges project-derived repo edges with file-config repo edges (version pattern tokens).</summary>
    private async Task PersistRepositoryDependencyLevelAndDependenciesAsync(
        int workspaceId,
        List<WorkspaceProject> workspaceProjects,
        List<(int DependentProjectId, int ReferencedProjectId, string? Version)> uniqueEdges,
        CancellationToken cancellationToken)
    {
        var links = await dbContext.WorkspaceRepositories
            .Include(wr => wr.Repository)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0) return;

        var repoIdsInWorkspace = links.Select(l => l.RepositoryId).ToHashSet();
        var versionByRepo = links.ToDictionary(x => x.RepositoryId, x => x.GitVersion, null);
        var nameToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var wr in links)
        {
            if (wr.Repository != null && !string.IsNullOrEmpty(wr.Repository.RepositoryName))
            {
                var name = wr.Repository.RepositoryName.Trim();
                if (!nameToRepoId.ContainsKey(name))
                    nameToRepoId[name] = wr.RepositoryId;
            }
        }

        var byProject = workspaceProjects.Count > 0 ? workspaceProjects.ToDictionary(p => p.ProjectId) : new Dictionary<int, WorkspaceProject>();
        var edgeSets = await BuildRepoDependencyEdgeSetsAsync(
            workspaceId,
            repoIdsInWorkspace,
            nameToRepoId,
            uniqueEdges,
            byProject,
            cancellationToken);
        var allRepoEdges = edgeSets.All;

        var inDegree = repoIdsInWorkspace.ToDictionary(id => id, _ => 0);
        var revEdges = repoIdsInWorkspace.ToDictionary(id => id, _ => new List<int>());
        foreach (var (depRepoId, refRepoId) in allRepoEdges)
        {
            if (!repoIdsInWorkspace.Contains(depRepoId) || !repoIdsInWorkspace.Contains(refRepoId)) continue;
            inDegree[depRepoId]++;
            revEdges[refRepoId].Add(depRepoId);
        }

        var levelByRepo = new Dictionary<int, int>();
        var queue = new Queue<int>(repoIdsInWorkspace.Where(rid => inDegree[rid] == 0));
        var currentLevel = 1;
        var remaining = repoIdsInWorkspace.Count;
        while (queue.Count > 0)
        {
            var levelSize = queue.Count;
            for (var i = 0; i < levelSize; i++)
            {
                var repoId = queue.Dequeue();
                levelByRepo[repoId] = currentLevel;
                remaining--;
                foreach (var depRepoId in revEdges[repoId])
                {
                    inDegree[depRepoId]--;
                    if (inDegree[depRepoId] == 0)
                        queue.Enqueue(depRepoId);
                }
            }
            currentLevel++;
        }

        var depCountByRepo = repoIdsInWorkspace.ToDictionary(id => id, _ => 0);
        foreach (var (depRepoId, _) in allRepoEdges)
            depCountByRepo[depRepoId] = depCountByRepo.GetValueOrDefault(depRepoId, 0) + 1;

        var unmatchedCountByRepo = repoIdsInWorkspace.ToDictionary(id => id, _ => 0);
        var unmatchedRepoEdges = new HashSet<(int DepRepoId, int RefRepoId)>();
        foreach (var (depId, refId, version) in uniqueEdges)
        {
            if (!byProject.TryGetValue(depId, out var depProj) || !byProject.TryGetValue(refId, out var refProj)) continue;
            if (depProj.RepositoryId == refProj.RepositoryId) continue;
            if (!repoIdsInWorkspace.Contains(depProj.RepositoryId) || !repoIdsInWorkspace.Contains(refProj.RepositoryId)) continue;
            var refRepoVersion = versionByRepo.GetValueOrDefault(refProj.RepositoryId);
            var depVersion = version?.Trim() ?? "";
            var refVersion = refRepoVersion?.Trim() ?? "";
            if (depVersion != refVersion)
                unmatchedRepoEdges.Add((depProj.RepositoryId, refProj.RepositoryId));
        }
        foreach (var (depRepoId, _) in unmatchedRepoEdges)
            unmatchedCountByRepo[depRepoId] = unmatchedCountByRepo.GetValueOrDefault(depRepoId, 0) + 1;

        int? dependencyLevelForRepo(int repoId)
        {
            if (remaining != 0) return null;
            return levelByRepo.TryGetValue(repoId, out var l) ? l : (int?)null;
        }

        foreach (var link in links)
        {
            link.DependencyLevel = dependencyLevelForRepo(link.RepositoryId);
            link.Dependencies = depCountByRepo.GetValueOrDefault(link.RepositoryId, 0);
            link.UnmatchedDeps = unmatchedCountByRepo.GetValueOrDefault(link.RepositoryId, 0);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Persistence: WorkspaceRepositories DependencyLevel/Dependencies. WorkspaceId={WorkspaceId}, LinkCount={Count}", workspaceId, links.Count);
    }
}
