using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Common.FileVersions;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Versions;

/// <summary>A package of a producer that a csproj consumer in the context references, and the connector it is mapped to.</summary>
public sealed record RequiredPackage(string PackageId, int? MatchedConnectorId);

/// <summary>Who in one context depends on a repository's version (design 3.1).</summary>
public sealed record VersionConsumers(IReadOnlyList<RequiredPackage> RequiredPackages, bool HasTokenConsumer)
{
    public static VersionConsumers None { get; } = new([], false);

    /// <summary>True when another repository references a package of it, or stamps its version with a <c>{@Repo}</c> token.</summary>
    public bool HasVersionConsumer => RequiredPackages.Count > 0 || HasTokenConsumer;
}

/// <summary>
/// Reads, for one context, which repositories have version consumers and which of their packages are required: csproj
/// package edges (physical and generated packages, <c>PackageId</c> else <c>ProjectName</c>, as the push plan names
/// them) and version-file <c>{@Repo}</c> token edges. Custom ordering edges do not count. Edges into the Workspace-role
/// repository are dropped, as everywhere else. Keyed by RepositoryId.
/// </summary>
public static class VersionConsumerReader
{
    public static async Task<Dictionary<int, VersionConsumers>> LoadAsync(
        AppDbContext db,
        int workspaceId,
        int workspaceFeatureContextId,
        bool isSpecialWorkspace,
        CancellationToken cancellationToken)
    {
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.Role, Name = wr.Repository!.RepositoryName })
            .ToListAsync(cancellationToken);
        var repoIds = links.Select(l => l.RepositoryId).ToHashSet();
        var workspaceRoleRepoIds = links.Where(l => l.Role == WorkspaceRepositoryRole.Workspace).Select(l => l.RepositoryId).ToHashSet();
        var nameToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links.Where(l => !string.IsNullOrWhiteSpace(l.Name)))
            nameToRepoId.TryAdd(link.Name!.Trim(), link.RepositoryId);

        var projects = await db.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && (p.WorkspaceFeatureContextId == workspaceFeatureContextId || p.IsGenerated))
            .Select(p => new { p.ProjectId, p.RepositoryId, p.PackageId, p.ProjectName, p.MatchedConnectorId })
            .ToListAsync(cancellationToken);
        var byProject = projects.ToDictionary(p => p.ProjectId);
        var projectIds = byProject.Keys.ToHashSet();
        var edges = projectIds.Count == 0
            ? []
            : await db.ProjectDependencies
                .AsNoTracking()
                .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
                .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
                .ToListAsync(cancellationToken);

        var packagesByRepo = new Dictionary<int, Dictionary<string, int?>>();
        foreach (var edge in edges)
        {
            if (!byProject.TryGetValue(edge.DependentProjectId, out var dep) || !byProject.TryGetValue(edge.ReferencedProjectId, out var referenced))
                continue;
            if (dep.RepositoryId == referenced.RepositoryId || !repoIds.Contains(dep.RepositoryId) || !repoIds.Contains(referenced.RepositoryId))
                continue;
            if (workspaceRoleRepoIds.Contains(referenced.RepositoryId))
                continue;
            var packageId = (!string.IsNullOrWhiteSpace(referenced.PackageId) ? referenced.PackageId : referenced.ProjectName)?.Trim();
            if (string.IsNullOrEmpty(packageId))
                continue;

            if (!packagesByRepo.TryGetValue(referenced.RepositoryId, out var packages))
                packagesByRepo[referenced.RepositoryId] = packages = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            // The same package id from a physical and a generated project: a known mapping wins over none.
            if (!packages.TryGetValue(packageId, out var known) || known is null)
                packages[packageId] = referenced.MatchedConnectorId;
        }

        var tokenConsumed = await LoadTokenConsumedRepoIdsAsync(
            db, workspaceId, workspaceFeatureContextId, isSpecialWorkspace, repoIds, nameToRepoId, workspaceRoleRepoIds, cancellationToken);

        var result = new Dictionary<int, VersionConsumers>();
        foreach (var repoId in repoIds)
        {
            var required = packagesByRepo.TryGetValue(repoId, out var packages)
                ? packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new RequiredPackage(p.Key, p.Value)).ToList()
                : [];
            result[repoId] = new VersionConsumers(required, tokenConsumed.Contains(repoId));
        }

        return result;
    }

    private static async Task<HashSet<int>> LoadTokenConsumedRepoIdsAsync(
        AppDbContext db,
        int workspaceId,
        int workspaceFeatureContextId,
        bool isSpecialWorkspace,
        HashSet<int> repoIds,
        Dictionary<string, int> nameToRepoId,
        HashSet<int> workspaceRoleRepoIds,
        CancellationToken cancellationToken)
    {
        var configs = await db.WorkspaceFileVersionConfigs
            .AsNoTracking()
            .Where(c => c.File != null && c.File.WorkspaceId == workspaceId)
            .Select(c => new { c.FileId, c.VersionPattern, c.File!.RepositoryId, c.File.IsMissingOnDisk })
            .ToListAsync(cancellationToken);
        var result = new HashSet<int>();
        if (configs.Count == 0)
            return result;

        var fileIds = configs.Select(c => c.FileId).Distinct().ToList();
        var missingByFileId = await db.WorkspaceFileContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == workspaceFeatureContextId && fileIds.Contains(s.FileId))
            .ToDictionaryAsync(s => s.FileId, s => s.IsMissingOnDisk, cancellationToken);

        foreach (var config in configs)
        {
            // Same "missing on disk" rule as the dependency graph: the context's own row, the shared flag only for the special Workspace.
            var missing = missingByFileId.TryGetValue(config.FileId, out var flag)
                ? flag == true
                : isSpecialWorkspace && config.IsMissingOnDisk == true;
            if (missing || !repoIds.Contains(config.RepositoryId))
                continue;

            foreach (var token in FileVersionTokenParser.ExtractTokens(config.VersionPattern))
            {
                if (token.Kind != FileVersionTokenKind.GitVersion)
                    continue;
                if (!nameToRepoId.TryGetValue(token.RepositoryName, out var producerId) || producerId == config.RepositoryId)
                    continue;
                if (workspaceRoleRepoIds.Contains(producerId))
                    continue;
                result.Add(producerId);
            }
        }

        return result;
    }
}
