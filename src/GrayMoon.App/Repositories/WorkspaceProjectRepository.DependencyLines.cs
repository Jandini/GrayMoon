using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
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

    /// <summary>Returns, per dependent repository, the distinct workspace-internal package dependencies (PackageId + version as written in the .csproj). Used to populate the dependency-badge tooltip for repositories whose dependencies are up to date.</summary>
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

    /// <summary>Package dependency lines for a single repository (badge tooltip).</summary>
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

    /// <summary>Mismatched package dependency lines for a single repository (badge tooltip).</summary>
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
}
