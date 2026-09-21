using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
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

    /// <summary>Returns repository-level dependency graph (nodes = repos, edges = repo depends on repo). Includes project-derived, file-config, and custom dependency edges. For Cytoscape.</summary>
    public async Task<RepositoryDependencyGraph> GetRepositoryDependencyGraphAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0) return new RepositoryDependencyGraph(new List<RepositoryDependencyNode>(), new List<RepositoryDependencyEdge>());

        var repoIdsInWorkspace = links.Select(l => l.RepositoryId).ToHashSet();
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

        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);
        var projectEdges = await GetDependencyEdgesAsync(workspaceId, cancellationToken);
        var uniqueEdges = projectEdges
            .Where(e => byProject.ContainsKey(e.DependentProjectId) && byProject.ContainsKey(e.ReferencedProjectId))
            .Select(e => (e.DependentProjectId, e.ReferencedProjectId, (string?)null))
            .ToList();

        var edgeSets = await BuildRepoDependencyEdgeSetsAsync(
            workspaceId,
            repoIdsInWorkspace,
            nameToRepoId,
            uniqueEdges,
            byProject,
            cancellationToken);

        var repoNodes = links
            .Where(wr => wr.Repository != null && !string.IsNullOrEmpty(wr.Repository.RepositoryName))
            .Select(wr => new RepositoryDependencyNode(wr.RepositoryId, wr.Repository!.RepositoryName!, wr.RepositoryType))
            .ToList();

        var edgeList = edgeSets.All.Select(e => new RepositoryDependencyEdge(e.DepRepoId, e.RefRepoId)).ToList();

        return new RepositoryDependencyGraph(repoNodes, edgeList);
    }

    /// <summary>Returns referenced repository IDs derived from csproj and file-version config for the given dependent repository (implicit, non-custom edges).</summary>
    public async Task<HashSet<int>> GetImplicitReferencedRepoIdsAsync(
        int workspaceId,
        int dependentRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var bySource = await GetImplicitReferencedRepoIdsBySourceAsync(workspaceId, dependentRepositoryId, cancellationToken);
        var result = new HashSet<int>(bySource.FromProject);
        result.UnionWith(bySource.FromFile);
        result.UnionWith(bySource.FromPackage);
        return result;
    }

    /// <summary>Returns implicit referenced repository IDs for the given dependent, split by csproj vs version-file source.</summary>
    public async Task<ImplicitReferencedRepoIdsBySource> GetImplicitReferencedRepoIdsBySourceAsync(
        int workspaceId,
        int dependentRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var graph = await LoadWorkspaceRepoDependencyGraphAsync(workspaceId, cancellationToken);
        if (graph is null || !graph.RepoIdsInWorkspace.Contains(dependentRepositoryId))
            return new ImplicitReferencedRepoIdsBySource([], [], []);

        var fromProject = new HashSet<int>();
        foreach (var (dep, @ref) in graph.EdgeSets.ProjectDerived)
        {
            if (dep == dependentRepositoryId)
                fromProject.Add(@ref);
        }

        var fromFile = new HashSet<int>();
        foreach (var (dep, @ref) in graph.EdgeSets.FileConfig)
        {
            if (dep == dependentRepositoryId)
                fromFile.Add(@ref);
        }

        var fromPackage = new HashSet<int>();
        foreach (var (dep, @ref) in graph.EdgeSets.GeneratedPackage)
        {
            if (dep == dependentRepositoryId)
                fromPackage.Add(@ref);
        }

        return new ImplicitReferencedRepoIdsBySource(fromProject, fromFile, fromPackage);
    }

    /// <summary>
    /// Repo IDs that must not appear in the custom-dependencies picker for the given dependent
    /// because adding them would create a cycle. Already-existing dependencies of the dependent are excluded from this set.
    /// </summary>
    public async Task<HashSet<int>> GetCircularCustomDependencyRepoIdsAsync(
        int workspaceId,
        int dependentRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var graph = await LoadWorkspaceRepoDependencyGraphAsync(workspaceId, cancellationToken);
        if (graph is null || !graph.RepoIdsInWorkspace.Contains(dependentRepositoryId))
            return [];

        var allEdges = graph.EdgeSets.All.ToHashSet();
        var forwardAdjacency = BuildForwardAdjacency(allEdges);
        var existingDepsOfDependent = allEdges
            .Where(e => e.DepRepoId == dependentRepositoryId)
            .Select(e => e.RefRepoId)
            .ToHashSet();

        var circular = new HashSet<int>();
        foreach (var candidateRepoId in graph.RepoIdsInWorkspace)
        {
            if (candidateRepoId == dependentRepositoryId)
                continue;
            if (existingDepsOfDependent.Contains(candidateRepoId))
                continue;
            if (WouldCreateCycleOnAdd(allEdges, forwardAdjacency, dependentRepositoryId, candidateRepoId))
                circular.Add(candidateRepoId);
        }

        return circular;
    }

    private static Dictionary<int, List<int>> BuildForwardAdjacency(HashSet<(int DepRepoId, int RefRepoId)> edges)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (dep, @ref) in edges)
        {
            if (!adjacency.TryGetValue(dep, out var refs))
            {
                refs = [];
                adjacency[dep] = refs;
            }

            refs.Add(@ref);
        }

        return adjacency;
    }

    private static bool WouldCreateCycleOnAdd(
        HashSet<(int DepRepoId, int RefRepoId)> edges,
        Dictionary<int, List<int>> forwardAdjacency,
        int dependentRepositoryId,
        int candidateRepositoryId)
    {
        if (dependentRepositoryId == candidateRepositoryId)
            return true;
        if (edges.Contains((dependentRepositoryId, candidateRepositoryId)))
            return false;

        var visited = new HashSet<int> { candidateRepositoryId };
        var queue = new Queue<int>();
        queue.Enqueue(candidateRepositoryId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == dependentRepositoryId)
                return true;
            if (!forwardAdjacency.TryGetValue(current, out var refs))
                continue;
            foreach (var next in refs)
            {
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        return false;
    }

    private sealed record WorkspaceRepoDependencyGraph(
        HashSet<int> RepoIdsInWorkspace,
        RepoDependencyEdgeSets EdgeSets);

    private async Task<WorkspaceRepoDependencyGraph?> LoadWorkspaceRepoDependencyGraphAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return null;

        var repoIdsInWorkspace = links.Select(l => l.RepositoryId).ToHashSet();
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

        var projects = await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);
        var projectEdges = await GetDependencyEdgesAsync(workspaceId, cancellationToken);
        var uniqueEdges = projectEdges
            .Where(e => byProject.ContainsKey(e.DependentProjectId) && byProject.ContainsKey(e.ReferencedProjectId))
            .Select(e => (e.DependentProjectId, e.ReferencedProjectId, (string?)null))
            .ToList();

        var edgeSets = await BuildRepoDependencyEdgeSetsAsync(
            workspaceId,
            repoIdsInWorkspace,
            nameToRepoId,
            uniqueEdges,
            byProject,
            cancellationToken);

        return new WorkspaceRepoDependencyGraph(repoIdsInWorkspace, edgeSets);
    }

    private sealed record RepoDependencyEdgeSets(
        HashSet<(int DepRepoId, int RefRepoId)> ProjectDerived,
        HashSet<(int DepRepoId, int RefRepoId)> GeneratedPackage,
        HashSet<(int DepRepoId, int RefRepoId)> FileConfig,
        HashSet<(int DepRepoId, int RefRepoId)> Custom,
        List<(int DepRepoId, int RefRepoId)> All);

    private async Task<RepoDependencyEdgeSets> BuildRepoDependencyEdgeSetsAsync(
        int workspaceId,
        HashSet<int> repoIdsInWorkspace,
        Dictionary<string, int> nameToRepoId,
        List<(int DependentProjectId, int ReferencedProjectId, string? Version)> uniqueEdges,
        Dictionary<int, WorkspaceProject> byProject,
        CancellationToken cancellationToken,
        int? workspaceFeatureContextId = null)
    {
        // Split project-derived edges into real (physical .csproj to .csproj) and generated-package edges (the
        // referenced project is a virtual/generated WorkspaceProject inferred from a configured .csproj version
        // file) so the Custom Dependencies dialog can badge them distinctly, even though both flow into the same
        // dependency-level/push/restore machinery via the "All" union below.
        var projectDerivedRepoEdges = new HashSet<(int DepRepoId, int RefRepoId)>();
        var generatedPackageRepoEdges = new HashSet<(int DepRepoId, int RefRepoId)>();
        foreach (var (depId, refId, _) in uniqueEdges)
        {
            if (!byProject.TryGetValue(depId, out var depProj) || !byProject.TryGetValue(refId, out var refProj)) continue;
            if (depProj.RepositoryId == refProj.RepositoryId) continue;
            if (!repoIdsInWorkspace.Contains(depProj.RepositoryId) || !repoIdsInWorkspace.Contains(refProj.RepositoryId)) continue;

            if (refProj.IsGenerated)
                generatedPackageRepoEdges.Add((depProj.RepositoryId, refProj.RepositoryId));
            else
                projectDerivedRepoEdges.Add((depProj.RepositoryId, refProj.RepositoryId));
        }

        var fileConfigRepoEdges = new HashSet<(int DepRepoId, int RefRepoId)>();
        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);

        // Context-scoped "missing on disk" check (§7/AGENTS.md "Feature-context scoping") when a context was
        // supplied: WorkspaceFile.IsMissingOnDisk alone is the special Workspace's own answer, so a Feature's
        // file-config dependency edges must instead read that Feature's own WorkspaceFileContextState row
        // (falling back to the shared flag only when no such row exists and this is the special Workspace).
        // Callers that did not pass a context (existing dependency-graph-visualization call sites, unchanged
        // in this pass) keep the legacy shared-flag-only behavior.
        Dictionary<int, bool?>? missingFlagsByFileId = null;
        if (workspaceFeatureContextId is int ctxId)
        {
            var isSpecialWorkspace = await dbContext.WorkspaceFeatureContexts
                .AsNoTracking()
                .Where(c => c.WorkspaceFeatureContextId == ctxId)
                .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
                .FirstOrDefaultAsync(cancellationToken);
            var fileIds = configs.Where(c => c.File != null).Select(c => c.File!.FileId).Distinct().ToList();
            var states = await dbContext.WorkspaceFileContextStates
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == ctxId && fileIds.Contains(s.FileId))
                .ToDictionaryAsync(s => s.FileId, cancellationToken);
            missingFlagsByFileId = new Dictionary<int, bool?>();
            foreach (var fileId in fileIds)
            {
                if (states.TryGetValue(fileId, out var state))
                    missingFlagsByFileId[fileId] = state.IsMissingOnDisk;
                else if (isSpecialWorkspace)
                    missingFlagsByFileId[fileId] = configs.First(c => c.File!.FileId == fileId).File!.IsMissingOnDisk;
                else
                    missingFlagsByFileId[fileId] = null;
            }
        }

        foreach (var cfg in configs)
        {
            var isMissing = missingFlagsByFileId != null
                ? cfg.File != null && (missingFlagsByFileId.GetValueOrDefault(cfg.File.FileId) == true)
                : cfg.File?.IsMissingOnDisk == true;
            if (isMissing) continue;
            var fileRepoId = cfg.File?.RepositoryId;
            if (!fileRepoId.HasValue || fileRepoId.Value == 0 || !repoIdsInWorkspace.Contains(fileRepoId.Value)) continue;
            var dependentRepoId = fileRepoId.Value;
            var tokens = WorkspaceFileVersionService.ExtractTokens(cfg.VersionPattern);
            foreach (var token in tokens)
            {
                if (!nameToRepoId.TryGetValue(token.RepositoryName, out var referencedRepoId)) continue;
                if (referencedRepoId == dependentRepoId) continue;
                fileConfigRepoEdges.Add((dependentRepoId, referencedRepoId));
            }
        }

        var customRepoEdges = await customDependencyRepository.GetRepoEdgesByWorkspaceIdAsync(workspaceId, cancellationToken);

        var allRepoEdges = projectDerivedRepoEdges
            .Union(generatedPackageRepoEdges)
            .Union(fileConfigRepoEdges)
            .Union(customRepoEdges)
            .Distinct()
            .ToList();

        return new RepoDependencyEdgeSets(projectDerivedRepoEdges, generatedPackageRepoEdges, fileConfigRepoEdges, customRepoEdges, allRepoEdges);
    }
}
