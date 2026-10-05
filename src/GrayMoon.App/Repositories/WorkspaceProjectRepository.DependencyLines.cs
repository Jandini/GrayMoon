using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Returns dependency edges (DependentProjectId, ReferencedProjectId) for the workspace, across every Feature context. Prefer the context-scoped overload below.</summary>
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

    /// <summary>Returns dependency edges (DependentProjectId, ReferencedProjectId) scoped to a single context (null reproduces the legacy, unscoped behavior above). Suitable for Cytoscape: nodes = projects, edges = this list.</summary>
    public async Task<List<(int DependentProjectId, int ReferencedProjectId)>> GetDependencyEdgesAsync(int workspaceId, WorkspaceFeatureContextId? contextId, CancellationToken cancellationToken = default)
    {
        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0) return new List<(int, int)>();

        var idSet = scopedProjects.Select(p => p.ProjectId).ToHashSet();
        var rows = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => idSet.Contains(d.DependentProjectId) && idSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.DependentProjectId, r.ReferencedProjectId)).ToList();
    }

    /// <summary>Returns, per dependent repository, the distinct workspace-internal package dependencies (PackageId + version as written in the .csproj), across every Feature context. Prefer the context-scoped overload below.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string Version)>>> GetPackageDependencyLinesByRepoAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .Select(p => new { p.ProjectId, p.RepositoryId, p.PackageId })
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
            return new Dictionary<int, IReadOnlyList<(string, string)>>();

        var byProjectId = projects.ToDictionary(p => p.ProjectId);
        var projectIdSet = byProjectId.Keys.ToHashSet();

        var edges = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIdSet.Contains(d.DependentProjectId) && projectIdSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
            .ToListAsync(cancellationToken);

        var perRepo = new Dictionary<int, HashSet<(string PackageId, string Version)>>();
        foreach (var e in edges)
        {
            if (!byProjectId.TryGetValue(e.DependentProjectId, out var dep) || !byProjectId.TryGetValue(e.ReferencedProjectId, out var refProj))
                continue;
            var packageId = refProj.PackageId;
            if (string.IsNullOrWhiteSpace(packageId))
                continue;
            var version = e.Version ?? string.Empty;
            if (!perRepo.TryGetValue(dep.RepositoryId, out var set))
            {
                set = new HashSet<(string, string)>();
                perRepo[dep.RepositoryId] = set;
            }
            set.Add((packageId!, version));
        }

        return perRepo.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<(string PackageId, string Version)>)kv.Value
                .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Version, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>Returns, per dependent repository, the distinct workspace-internal package dependencies scoped to a single context (null reproduces the legacy, unscoped behavior above).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string Version)>>> GetPackageDependencyLinesByRepoAsync(int workspaceId, WorkspaceFeatureContextId? contextId, CancellationToken cancellationToken = default)
    {
        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0)
            return new Dictionary<int, IReadOnlyList<(string, string)>>();

        var byProjectId = scopedProjects.ToDictionary(p => p.ProjectId, p => new { p.RepositoryId, p.PackageId });
        var projectIdSet = byProjectId.Keys.ToHashSet();

        var edges = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIdSet.Contains(d.DependentProjectId) && projectIdSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId, d.Version })
            .ToListAsync(cancellationToken);

        var perRepo = new Dictionary<int, HashSet<(string PackageId, string Version)>>();
        foreach (var e in edges)
        {
            if (!byProjectId.TryGetValue(e.DependentProjectId, out var dep) || !byProjectId.TryGetValue(e.ReferencedProjectId, out var refProj))
                continue;
            var packageId = refProj.PackageId;
            if (string.IsNullOrWhiteSpace(packageId))
                continue;
            var version = e.Version ?? string.Empty;
            if (!perRepo.TryGetValue(dep.RepositoryId, out var set))
            {
                set = new HashSet<(string, string)>();
                perRepo[dep.RepositoryId] = set;
            }
            set.Add((packageId!, version));
        }

        return perRepo.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<(string PackageId, string Version)>)kv.Value
                .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Version, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>Package dependency lines for a single repository (badge tooltip), across every Feature context. Prefer the context-scoped overload below.</summary>
    public async Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageDependencyLinesForRepoAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        var depProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.RepositoryId == repositoryId)
            .Select(p => p.ProjectId)
            .ToListAsync(cancellationToken);
        if (depProjects.Count == 0)
            return Array.Empty<(string, string)>();

        var depProjectIds = depProjects.ToHashSet();
        var edges = await (
            from d in dbContext.ProjectDependencies.AsNoTracking()
            join refProj in dbContext.WorkspaceProjects.AsNoTracking() on d.ReferencedProjectId equals refProj.ProjectId
            where depProjectIds.Contains(d.DependentProjectId)
                && refProj.WorkspaceId == workspaceId
                && refProj.PackageId != null
                && refProj.PackageId != string.Empty
            select new { PackageId = refProj.PackageId!, Version = d.Version ?? string.Empty })
            .ToListAsync(cancellationToken);

        return edges
            .Select(e => (e.PackageId, e.Version))
            .Distinct()
            .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Package dependency lines for a single repository (badge tooltip), scoped to a single context (null reproduces the legacy, unscoped behavior above). Both the dependent repository's own projects and the referenced package-producing projects are restricted to the same context, so a Feature's tooltip never shows a dependency that belongs only to the Workspace or to another Feature.</summary>
    public async Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageDependencyLinesForRepoAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken = default)
    {
        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0)
            return Array.Empty<(string, string)>();

        var scopedProjectIds = scopedProjects.Select(p => p.ProjectId).ToHashSet();
        var depProjectIds = scopedProjects
            .Where(p => p.RepositoryId == repositoryId)
            .Select(p => p.ProjectId)
            .ToHashSet();
        if (depProjectIds.Count == 0)
            return Array.Empty<(string, string)>();

        var edges = await (
            from d in dbContext.ProjectDependencies.AsNoTracking()
            join refProj in dbContext.WorkspaceProjects.AsNoTracking() on d.ReferencedProjectId equals refProj.ProjectId
            where depProjectIds.Contains(d.DependentProjectId)
                && scopedProjectIds.Contains(refProj.ProjectId)
                && refProj.PackageId != null
                && refProj.PackageId != string.Empty
            select new { PackageId = refProj.PackageId!, Version = d.Version ?? string.Empty })
            .ToListAsync(cancellationToken);

        return edges
            .Select(e => (e.PackageId, e.Version))
            .Distinct()
            .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Mismatched package dependency lines for a single repository (badge tooltip), across every Feature context and always compared against the shared link's GitVersion. Prefer the context-scoped overload below.</summary>
    public async Task<IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>> GetMismatchedDependencyLinesForRepoAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        var versionByRepo = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.GitVersion })
            .ToDictionaryAsync(x => x.RepositoryId, x => x.GitVersion, cancellationToken);

        var depProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.RepositoryId == repositoryId)
            .Select(p => p.ProjectId)
            .ToListAsync(cancellationToken);
        if (depProjects.Count == 0)
            return Array.Empty<(string, string, string)>();

        var depProjectIds = depProjects.ToHashSet();
        var edges = await (
            from d in dbContext.ProjectDependencies.AsNoTracking()
            join refProj in dbContext.WorkspaceProjects.AsNoTracking() on d.ReferencedProjectId equals refProj.ProjectId
            where depProjectIds.Contains(d.DependentProjectId) && refProj.WorkspaceId == workspaceId
            select new
            {
                RefRepoId = refProj.RepositoryId,
                PackageId = !string.IsNullOrWhiteSpace(refProj.PackageId) ? refProj.PackageId! : refProj.ProjectName,
                Version = d.Version,
            })
            .ToListAsync(cancellationToken);

        var lines = new HashSet<(string PackageId, string CurrentVersion, string NewVersion)>();
        foreach (var e in edges)
        {
            if (string.IsNullOrWhiteSpace(e.PackageId))
                continue;
            var refVersion = versionByRepo.GetValueOrDefault(e.RefRepoId)?.Trim() ?? "";
            var depVersion = e.Version?.Trim() ?? "";
            if (string.IsNullOrEmpty(refVersion) || depVersion == refVersion)
                continue;
            lines.Add((e.PackageId.Trim(), depVersion, refVersion));
        }

        return lines
            .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Mismatched package dependency lines for a single repository (badge tooltip), scoped to a single context (null reproduces the legacy, unscoped behavior above). For a Feature context the comparison uses that Feature's own checked-out version (<see cref="WorkspaceRepositoryContextState.GitVersion"/>), never the shared link's <see cref="WorkspaceRepositoryLink.GitVersion"/>; a repository with no context-state row yet has an unknown version (no mismatch reported for it), never the Workspace's value.</summary>
    public async Task<IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>> GetMismatchedDependencyLinesForRepoAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken = default)
    {
        var versionByRepo = await GetRepositoryVersionMapAsync(workspaceId, contextId, cancellationToken);

        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0)
            return Array.Empty<(string, string, string)>();

        var scopedProjectIds = scopedProjects.Select(p => p.ProjectId).ToHashSet();
        var depProjectIds = scopedProjects
            .Where(p => p.RepositoryId == repositoryId)
            .Select(p => p.ProjectId)
            .ToHashSet();
        if (depProjectIds.Count == 0)
            return Array.Empty<(string, string, string)>();

        var edges = await (
            from d in dbContext.ProjectDependencies.AsNoTracking()
            join refProj in dbContext.WorkspaceProjects.AsNoTracking() on d.ReferencedProjectId equals refProj.ProjectId
            where depProjectIds.Contains(d.DependentProjectId) && scopedProjectIds.Contains(refProj.ProjectId)
            select new
            {
                RefRepoId = refProj.RepositoryId,
                PackageId = !string.IsNullOrWhiteSpace(refProj.PackageId) ? refProj.PackageId! : refProj.ProjectName,
                Version = d.Version,
            })
            .ToListAsync(cancellationToken);

        var lines = new HashSet<(string PackageId, string CurrentVersion, string NewVersion)>();
        foreach (var e in edges)
        {
            if (string.IsNullOrWhiteSpace(e.PackageId))
                continue;
            var refVersion = versionByRepo.GetValueOrDefault(e.RefRepoId)?.Trim() ?? "";
            var depVersion = e.Version?.Trim() ?? "";
            if (string.IsNullOrEmpty(refVersion) || depVersion == refVersion)
                continue;
            lines.Add((e.PackageId.Trim(), depVersion, refVersion));
        }

        return lines
            .OrderBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Maps RepositoryId to its checked-out version for the given context: the shared link's GitVersion for the special Workspace (or when <paramref name="contextId"/> is null), otherwise the Feature's own <see cref="WorkspaceRepositoryContextState.GitVersion"/> (null, never the shared value, when that Feature has no context-state row yet - rule 2 of feature-context-scoping.mdc).</summary>
    private async Task<Dictionary<int, string?>> GetRepositoryVersionMapAsync(int workspaceId, WorkspaceFeatureContextId? contextId, CancellationToken cancellationToken)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.WorkspaceRepositoryId, wr.GitVersion })
            .ToListAsync(cancellationToken);

        if (contextId is null)
            return links.ToDictionary(x => x.RepositoryId, x => x.GitVersion);

        var cid = contextId.Value.Value;
        var isSpecialWorkspace = await IsSpecialWorkspaceContextAsync(cid, cancellationToken);
        if (isSpecialWorkspace)
            return links.ToDictionary(x => x.RepositoryId, x => x.GitVersion);

        var linkIds = links.Select(l => l.WorkspaceRepositoryId).ToHashSet();
        var contextVersions = await dbContext.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == cid && linkIds.Contains(s.WorkspaceRepositoryId))
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, s => s.GitVersion, cancellationToken);

        return links.ToDictionary(
            x => x.RepositoryId,
            x => contextVersions.TryGetValue(x.WorkspaceRepositoryId, out var version) ? version : null);
    }
}
