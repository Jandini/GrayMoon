using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Loads per-repo GitVersion and DependencyLevel scoped to a Feature context: read from <see cref="WorkspaceRepositoryContextState"/> when a row exists for that context, falling back to the shared <see cref="WorkspaceRepositoryLink"/> only for the special Workspace context (or when no context-state row has been persisted for that repo yet). Internal (not private) so other context-aware callers in the same assembly - e.g. <c>DependencyUpdateOrchestrator</c>'s per-level repo grouping - can reuse the same fallback rule instead of reading the shared link directly.</summary>
    internal async Task<(Dictionary<int, string?> VersionByRepo, Dictionary<int, int?> LevelByRepo)> GetContextVersionAndLevelByRepoAsync(
        int workspaceId,
        int workspaceFeatureContextId,
        CancellationToken cancellationToken)
    {
        var isSpecialWorkspace = await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.WorkspaceRepositoryId, wr.GitVersion, wr.DependencyLevel })
            .ToListAsync(cancellationToken);

        var states = await dbContext.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId
                && links.Select(l => l.WorkspaceRepositoryId).Contains(s.WorkspaceRepositoryId))
            .Select(s => new { s.WorkspaceRepositoryId, s.GitVersion, s.DependencyLevel })
            .ToListAsync(cancellationToken);
        var stateByLinkId = states.ToDictionary(s => s.WorkspaceRepositoryId);

        var versionByRepo = new Dictionary<int, string?>();
        var levelByRepo = new Dictionary<int, int?>();
        foreach (var link in links)
        {
            if (stateByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                versionByRepo[link.RepositoryId] = state.GitVersion;
                levelByRepo[link.RepositoryId] = state.DependencyLevel;
            }
            else if (isSpecialWorkspace)
            {
                versionByRepo[link.RepositoryId] = link.GitVersion;
                levelByRepo[link.RepositoryId] = link.DependencyLevel;
            }
            else
            {
                versionByRepo[link.RepositoryId] = null;
                levelByRepo[link.RepositoryId] = null;
            }
        }

        return (versionByRepo, levelByRepo);
    }

    /// <summary>Legacy overload for callers without a context id: resolves the special Workspace context.</summary>
    public async Task<List<SyncDependenciesRepoPayload>> GetSyncDependenciesPayloadAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await GetSyncDependenciesPayloadAsync(workspaceId, contextId, cancellationToken);
    }

    /// <summary>Returns payload for syncing dependency versions, scoped to <paramref name="workspaceFeatureContextId"/>: per repo, list of (project path, package ID to new version) for dependencies that do not match the referenced repo's checked-out version for that context.</summary>
    public async Task<List<SyncDependenciesRepoPayload>> GetSyncDependenciesPayloadAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var (versionByRepo, levelByRepo) = await GetContextVersionAndLevelByRepoAsync(workspaceId, workspaceFeatureContextId, cancellationToken);

        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId && (p.WorkspaceFeatureContextId == workspaceFeatureContextId || p.IsGenerated))
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
        var repoToProjectUpdates = new Dictionary<int, Dictionary<string, Dictionary<string, (string Current, string New)>>>(repoIds.Count);
        foreach (var repoId in repoIds)
            repoToProjectUpdates[repoId] = new Dictionary<string, Dictionary<string, (string Current, string New)>>(StringComparer.OrdinalIgnoreCase);

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
                packageDict = new Dictionary<string, (string Current, string New)>(StringComparer.OrdinalIgnoreCase);
                repoUpdates[projectPath] = packageDict;
            }
            packageDict[packageId] = (depVersion, refVersionNorm);
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

            var dependencyLevel = levelByRepo.GetValueOrDefault(repoId);
            var projectUpdates = projectUpdatesDict
                .Select(kv => new SyncDependenciesProjectUpdate(kv.Key, kv.Value.Select(p => (p.Key, p.Value.Current, p.Value.New)).ToList()))
                .ToList();
            result.Add(new SyncDependenciesRepoPayload(repoId, repoName, dependencyLevel, projectUpdates));
        }

        return result.OrderBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Legacy overload for callers without a context id: resolves the special Workspace context.</summary>
    public async Task<List<PushRepoPayload>> GetPushPlanPayloadAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await GetPushPlanPayloadAsync(workspaceId, contextId, cancellationToken);
    }

    /// <summary>Returns push plan for <paramref name="workspaceFeatureContextId"/>: all workspace repos with that context's dependency level and, for each repo, the list of (PackageId, Version, MatchedConnectorId) that it depends on from lower-level repos. Used for dependency-synchronized push.</summary>
    public async Task<List<PushRepoPayload>> GetPushPlanPayloadAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0) return new List<PushRepoPayload>();

        var (_, contextLevelByRepo) = await GetContextVersionAndLevelByRepoAsync(workspaceId, workspaceFeatureContextId, cancellationToken);

        var repoIdsInWorkspace = links.Select(l => l.RepositoryId).ToHashSet();
        var levelByRepo = contextLevelByRepo
            .Where(kv => kv.Value.HasValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        var maxLevel = levelByRepo.Values.DefaultIfEmpty(0).Max();
        int effectiveLevel(int repoId) => levelByRepo.TryGetValue(repoId, out var l) ? l : maxLevel + 1;

        var allProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId && (p.WorkspaceFeatureContextId == workspaceFeatureContextId || p.IsGenerated))
            .ToListAsync(cancellationToken);
        var projects = allProjects.Where(p => repoIdsInWorkspace.Contains(p.RepositoryId)).ToList();
        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var byProject = projects.ToDictionary(p => p.ProjectId);

        var dependencies = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
            .ToListAsync(cancellationToken);

        var repoToRequired = new Dictionary<int, List<RequiredPackageForPush>>();
        foreach (var link in links)
            repoToRequired[link.RepositoryId] = new List<RequiredPackageForPush>();

        var seenPerRepo = new Dictionary<int, HashSet<(string PackageId, string Version)>>();
        foreach (var link in links)
            seenPerRepo[link.RepositoryId] = new HashSet<(string, string)>();

        foreach (var d in dependencies)
        {
            if (!byProject.TryGetValue(d.DependentProjectId, out var depProj) || !byProject.TryGetValue(d.ReferencedProjectId, out var refProj))
                continue;
            var depLevel = effectiveLevel(depProj.RepositoryId);
            var refLevel = effectiveLevel(refProj.RepositoryId);
            if (refLevel >= depLevel)
                continue;
            var packageId = (!string.IsNullOrWhiteSpace(refProj.PackageId) ? refProj.PackageId : refProj.ProjectName)?.Trim() ?? "";
            var version = d.Version?.Trim() ?? "";
            if (string.IsNullOrEmpty(packageId)) continue;
            var key = (packageId.ToLowerInvariant(), version);
            if (seenPerRepo[depProj.RepositoryId].Contains(key)) continue;
            seenPerRepo[depProj.RepositoryId].Add(key);
            repoToRequired[depProj.RepositoryId].Add(new RequiredPackageForPush(packageId, version, refProj.MatchedConnectorId));
        }

        var result = new List<PushRepoPayload>();
        foreach (var link in links)
        {
            if (!string.IsNullOrWhiteSpace(link.CheckedOutTag))
                continue;
            var repo = link.Repository;
            var repoName = repo?.RepositoryName ?? "";
            if (string.IsNullOrEmpty(repoName)) continue;
            var required = repoToRequired[link.RepositoryId];
            result.Add(new PushRepoPayload(link.RepositoryId, repoName, levelByRepo.GetValueOrDefault(link.RepositoryId), required));
        }
        return result.OrderBy(r => r.DependencyLevel ?? int.MaxValue).ThenBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Legacy overload for callers without a context id: resolves the special Workspace context.</summary>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoAsync(int workspaceId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await GetPushDependencyInfoForRepoAsync(workspaceId, contextId, repositoryId, cancellationToken);
    }

    /// <summary>Gets push dependency info for a single repo scoped to <paramref name="workspaceFeatureContextId"/>: its payload, the repo IDs it depends on (lower level), and those repos' payloads in level order. Returns null if repo not in workspace.</summary>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoAsync(int workspaceId, int workspaceFeatureContextId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var fullPlan = await GetPushPlanPayloadAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
        var payloadByRepo = fullPlan.ToDictionary(p => p.RepoId);
        if (!payloadByRepo.TryGetValue(repositoryId, out var payloadForRepo))
            return null;

        if (payloadForRepo.RequiredPackages.Count == 0)
            return new PushDependencyInfoForRepo(payloadForRepo, Array.Empty<int>(), Array.Empty<PushRepoPayload>());

        var (_, contextLevelByRepo) = await GetContextVersionAndLevelByRepoAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
        var levelByRepo = contextLevelByRepo.Where(kv => kv.Value.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        var maxLevel = levelByRepo.Values.DefaultIfEmpty(0).Max();
        int effectiveLevel(int rId) => levelByRepo.TryGetValue(rId, out var l) ? l : maxLevel + 1;

        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && (p.WorkspaceFeatureContextId == workspaceFeatureContextId || p.IsGenerated))
            .ToListAsync(cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);
        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var dependencies = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);

        var myLevel = effectiveLevel(repositoryId);
        var dependencyRepoIds = new HashSet<int>();
        foreach (var d in dependencies)
        {
            if (!byProject.TryGetValue(d.DependentProjectId, out var depProj) || !byProject.TryGetValue(d.ReferencedProjectId, out var refProj))
                continue;
            if (depProj.RepositoryId != repositoryId) continue;
            var refLevel = effectiveLevel(refProj.RepositoryId);
            if (refLevel < myLevel)
                dependencyRepoIds.Add(refProj.RepositoryId);
        }

        var dependencyPathPayloads = fullPlan.Where(p => dependencyRepoIds.Contains(p.RepoId))
            .OrderBy(p => p.DependencyLevel ?? int.MaxValue)
            .ThenBy(p => p.RepoName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dependencyRepoIdsList = dependencyPathPayloads.Select(p => p.RepoId).ToList();

        var packageToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            var key = p.PackageId?.Trim();
            if (!string.IsNullOrEmpty(key) && !packageToRepoId.ContainsKey(key))
                packageToRepoId[key] = p.RepositoryId;
        }
        var packageIdToLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in payloadForRepo.RequiredPackages)
        {
            if (packageToRepoId.TryGetValue(pkg.PackageId.Trim(), out var repoId) && levelByRepo.TryGetValue(repoId, out var level))
                packageIdToLevel[pkg.PackageId.Trim()] = level;
        }
        return new PushDependencyInfoForRepo(payloadForRepo, dependencyRepoIdsList, dependencyPathPayloads, packageIdToLevel.Count > 0 ? packageIdToLevel : null);
    }

    /// <summary>Legacy overload for callers without a context id: resolves the special Workspace context.</summary>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoSetAsync(int workspaceId, IReadOnlySet<int> repoIds, CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await GetPushDependencyInfoForRepoSetAsync(workspaceId, contextId, repoIds, cancellationToken);
    }

    /// <summary>Gets push dependency info for a set of repos scoped to <paramref name="workspaceFeatureContextId"/>: merged required packages and dependency path (union of all repos' paths). Used for main Push button to show same modal as single-repo.</summary>
    /// <remarks>
    /// Computes the shared push-plan/links/projects/dependencies payload once (via <see cref="GetPushPlanPayloadAsync(int,int,CancellationToken)"/> plus one
    /// links, one projects, and one dependencies query below) and slices it per requested repo in memory, rather than calling
    /// <see cref="GetPushDependencyInfoForRepoAsync(int,int,int,CancellationToken)"/> once per repo - each such call re-ran that same ~3-query payload
    /// computation, making the previous version roughly 6xN+3 EF queries for N repos where this is O(1) in repo count.
    /// </remarks>
    public async Task<PushDependencyInfoForRepo?> GetPushDependencyInfoForRepoSetAsync(int workspaceId, int workspaceFeatureContextId, IReadOnlySet<int> repoIds, CancellationToken cancellationToken = default)
    {
        if (repoIds == null || repoIds.Count == 0) return null;
        var fullPlan = await GetPushPlanPayloadAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
        var payloadByRepo = fullPlan.ToDictionary(p => p.RepoId);
        var repoIdsList = repoIds.ToList();

        var allRequired = new List<RequiredPackageForPush>();
        foreach (var repoId in repoIdsList)
        {
            if (payloadByRepo.TryGetValue(repoId, out var payloadForRepo))
                allRequired.AddRange(payloadForRepo.RequiredPackages);
        }
        var mergedRequired = allRequired.DistinctBy(r => (r.PackageId, r.Version, r.MatchedConnectorId)).ToList();
        var syntheticPayload = new PushRepoPayload(
            0,
            repoIds.Count == 1 ? payloadByRepo.GetValueOrDefault(repoIdsList[0])?.RepoName ?? "1 repository" : $"{repoIds.Count} repositories",
            null,
            mergedRequired);

        if (mergedRequired.Count == 0)
            return new PushDependencyInfoForRepo(syntheticPayload, Array.Empty<int>(), Array.Empty<PushRepoPayload>());

        // Same shared fetch GetPushDependencyInfoForRepoAsync would otherwise re-run per repo.
        var (_, contextLevelByRepo) = await GetContextVersionAndLevelByRepoAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
        var levelByRepo = contextLevelByRepo.Where(kv => kv.Value.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        var maxLevel = levelByRepo.Values.DefaultIfEmpty(0).Max();
        int effectiveLevel(int rId) => levelByRepo.TryGetValue(rId, out var l) ? l : maxLevel + 1;

        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && (p.WorkspaceFeatureContextId == workspaceFeatureContextId || p.IsGenerated))
            .ToListAsync(cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);
        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var dependencies = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);

        // For each requested repo (that has required packages - matching GetPushDependencyInfoForRepoAsync's own
        // short-circuit when RequiredPackages is empty), find its lower-level dependency repos; union across the set.
        var dependencyRepoIds = new HashSet<int>();
        foreach (var repoId in repoIdsList)
        {
            if (!payloadByRepo.TryGetValue(repoId, out var payloadForRepo) || payloadForRepo.RequiredPackages.Count == 0)
                continue;

            var myLevel = effectiveLevel(repoId);
            foreach (var d in dependencies)
            {
                if (!byProject.TryGetValue(d.DependentProjectId, out var depProj) || !byProject.TryGetValue(d.ReferencedProjectId, out var refProj))
                    continue;
                if (depProj.RepositoryId != repoId) continue;
                var refLevel = effectiveLevel(refProj.RepositoryId);
                if (refLevel < myLevel)
                    dependencyRepoIds.Add(refProj.RepositoryId);
            }
        }

        var dependencyPathPayloads = fullPlan.Where(p => dependencyRepoIds.Contains(p.RepoId))
            .OrderBy(p => p.DependencyLevel ?? int.MaxValue)
            .ThenBy(p => p.RepoName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dependencyRepoIdsList = dependencyPathPayloads.Select(p => p.RepoId).ToList();

        var packageToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            var key = p.PackageId?.Trim();
            if (!string.IsNullOrEmpty(key) && !packageToRepoId.ContainsKey(key))
                packageToRepoId[key] = p.RepositoryId;
        }
        var packageLevelSource = fullPlan.ToDictionary(p => p.RepoId, p => p.DependencyLevel ?? int.MaxValue);
        var packageIdToLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in mergedRequired)
        {
            if (packageToRepoId.TryGetValue(pkg.PackageId.Trim(), out var repoId) && packageLevelSource.TryGetValue(repoId, out var level))
                packageIdToLevel[pkg.PackageId.Trim()] = level;
        }
        return new PushDependencyInfoForRepo(syntheticPayload, dependencyRepoIdsList, dependencyPathPayloads, packageIdToLevel.Count > 0 ? packageIdToLevel : null);
    }
}
