using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Recomputes DependencyLevel, Dependencies, and UnmatchedDeps for the special Workspace context and persists to WorkspaceRepositoryLink. Use after a repository version change (e.g. notify job) so the grid can refresh without re-reading .csproj files.</summary>
    public async Task RecomputeAndPersistRepositoryDependencyStatsAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, contextId, cancellationToken);
    }

    /// <summary>Recomputes DependencyLevel, Dependencies, and UnmatchedDeps from current DB state (workspace projects, project dependencies, and file version configs) scoped to <paramref name="workspaceFeatureContextId"/> and persists to <see cref="WorkspaceRepositoryContextState"/> for that context. The special Workspace context additionally mirrors the result onto <see cref="WorkspaceRepositoryLink"/> for legacy readers.</summary>
    public async Task RecomputeAndPersistRepositoryDependencyStatsAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var workspaceProjects = await GetByWorkspaceIdAsync(workspaceId, workspaceFeatureContextId, cancellationToken);

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

        await PersistRepositoryDependencyLevelAndDependenciesAsync(workspaceId, workspaceFeatureContextId, workspaceProjects, uniqueEdges, cancellationToken);
    }

    /// <summary>Computes dependency level and dependency count per repo for the given context and persists them onto <see cref="WorkspaceRepositoryContextState"/> (and, for the special Workspace context, onto <see cref="WorkspaceRepositoryLink"/> as well). Merges project-derived repo edges with file-config repo edges (version pattern tokens).</summary>
    private async Task PersistRepositoryDependencyLevelAndDependenciesAsync(
        int workspaceId,
        int workspaceFeatureContextId,
        List<WorkspaceProject> workspaceProjects,
        List<(int DependentProjectId, int ReferencedProjectId, string? Version)> uniqueEdges,
        CancellationToken cancellationToken)
    {
        var isSpecialWorkspace = await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var links = await dbContext.WorkspaceRepositories
            .Include(wr => wr.Repository)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0) return;

        var repoIdsInWorkspace = links.Select(l => l.RepositoryId).ToHashSet();

        // The Feature's own checked-out version is what unmatched-dependency comparisons must use; the shared
        // link's GitVersion is only correct for the special Workspace context (see design §6.4).
        var contextVersionByLinkId = await dbContext.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId
                && links.Select(l => l.WorkspaceRepositoryId).Contains(s.WorkspaceRepositoryId))
            .Select(s => new { s.WorkspaceRepositoryId, s.GitVersion })
            .ToListAsync(cancellationToken);
        var contextVersionByLink = contextVersionByLinkId.ToDictionary(x => x.WorkspaceRepositoryId, x => x.GitVersion);

        var versionByRepo = links.ToDictionary(
            x => x.RepositoryId,
            x => isSpecialWorkspace
                ? x.GitVersion
                : contextVersionByLink.GetValueOrDefault(x.WorkspaceRepositoryId, x.GitVersion));

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
            cancellationToken,
            workspaceFeatureContextId);
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

        var existingStates = await dbContext.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId
                && links.Select(l => l.WorkspaceRepositoryId).Contains(s.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var stateByLinkId = existingStates.ToDictionary(s => s.WorkspaceRepositoryId);

        foreach (var link in links)
        {
            var level = dependencyLevelForRepo(link.RepositoryId);
            var deps = depCountByRepo.GetValueOrDefault(link.RepositoryId, 0);
            var unmatched = unmatchedCountByRepo.GetValueOrDefault(link.RepositoryId, 0);

            if (!stateByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                state = new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = workspaceFeatureContextId,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    SyncStatus = RepoSyncStatus.NeedsSync
                };
                dbContext.WorkspaceRepositoryContextStates.Add(state);
            }

            state.DependencyLevel = level;
            state.Dependencies = deps;
            state.UnmatchedDeps = unmatched;

            if (isSpecialWorkspace)
            {
                link.DependencyLevel = level;
                link.Dependencies = deps;
                link.UnmatchedDeps = unmatched;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Persistence: WorkspaceRepositoryContextStates DependencyLevel/Dependencies. WorkspaceId={WorkspaceId}, ContextId={ContextId}, LinkCount={Count}", workspaceId, workspaceFeatureContextId, links.Count);
    }
}
