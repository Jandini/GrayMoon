using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>
    /// Returns, per package project (keyed by ProjectId), the other workspace repositories that restore it, directly or
    /// transitively through other workspace packages, scoped to a single context (null reproduces the legacy, unscoped
    /// behavior). The package's own repository is never listed. A package id published by several projects has no
    /// dependency-graph edge; the repositories that reference it are still listed, on every project that publishes it.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PackageDependentRepository>>> GetPackageDependentRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken = default)
    {
        var (nodes, edges) = await LoadPackageConsumerGraphAsync(workspaceId, contextId, cancellationToken);
        if (nodes.Count == 0)
            return new Dictionary<int, IReadOnlyList<PackageDependentRepository>>();
        return ResolvePackageDependentRepositories(nodes, edges);
    }

    /// <summary>
    /// Returns, per package project (keyed by ProjectId), the other workspace packages that projects in its repository
    /// restore, directly or transitively through other workspace packages. Other packages published by a referenced
    /// package's repository are included too, via that package. Every package row for a repository gets the same list.
    /// Packages published by that repository are omitted. A package id published by several projects is listed once.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PackageAccessNeed>>> GetPackageAccessNeedsAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken = default)
    {
        var (nodes, edges) = await LoadPackageConsumerGraphAsync(workspaceId, contextId, cancellationToken);
        if (nodes.Count == 0)
            return new Dictionary<int, IReadOnlyList<PackageAccessNeed>>();
        return ResolvePackageAccessNeeds(nodes, edges);
    }

    private async Task<(IReadOnlyList<PackageDependentsNode> Nodes, IReadOnlyList<(int DependentProjectId, int ReferencedProjectId)> Edges)> LoadPackageConsumerGraphAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken)
    {
        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0)
            return ([], []);

        var projectIdSet = scopedProjects.Select(p => p.ProjectId).ToHashSet();
        var edges = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIdSet.Contains(d.DependentProjectId) && projectIdSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);

        var usages = await dbContext.AmbiguousPackageReferences
            .AsNoTracking()
            .Where(u => projectIdSet.Contains(u.DependentProjectId))
            .Select(u => new { u.DependentProjectId, u.PackageId })
            .ToListAsync(cancellationToken);

        var nodes = scopedProjects
            .Select(p => new PackageDependentsNode(p.ProjectId, p.RepositoryId, p.Repository?.RepositoryName ?? string.Empty, p.PackageId))
            .ToList();
        var combined = edges.Select(e => (e.DependentProjectId, e.ReferencedProjectId)).ToList();
        combined.AddRange(EdgesForAmbiguousUsages(nodes, usages.Select(u => (u.DependentProjectId, u.PackageId))));
        return (nodes, combined);
    }

    /// <summary>
    /// One direct edge from the referencing project to every project that publishes the package id. These edges are
    /// not stored; the dependency graph keeps a single producer.
    /// </summary>
    private static List<(int DependentProjectId, int ReferencedProjectId)> EdgesForAmbiguousUsages(
        IReadOnlyList<PackageDependentsNode> projects,
        IEnumerable<(int DependentProjectId, string PackageId)> usages)
    {
        var producersByPackage = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var packageId = project.PackageId?.Trim();
            if (string.IsNullOrEmpty(packageId))
                continue;
            if (!producersByPackage.TryGetValue(packageId, out var producers))
                producersByPackage[packageId] = producers = [];
            producers.Add(project.ProjectId);
        }

        var edges = new List<(int DependentProjectId, int ReferencedProjectId)>();
        foreach (var (dependentProjectId, packageId) in usages)
        {
            if (!producersByPackage.TryGetValue(packageId.Trim(), out var producers))
                continue;
            foreach (var producerId in producers)
            {
                if (producerId != dependentProjectId)
                    edges.Add((dependentProjectId, producerId));
            }
        }
        return edges;
    }

    /// <summary>Project facts needed by <see cref="ResolvePackageDependentRepositories"/>.</summary>
    public sealed record PackageDependentsNode(int ProjectId, int RepositoryId, string RepositoryName, string? PackageId);

    /// <summary>
    /// Walks consumer edges up from every package project. A project that references the package makes its repository a
    /// direct dependent; when that project is itself a package, the repositories that restore it depend on the original
    /// package through it. A repository reached both ways is reported as direct.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<PackageDependentRepository>> ResolvePackageDependentRepositories(
        IReadOnlyList<PackageDependentsNode> projects,
        IReadOnlyList<(int DependentProjectId, int ReferencedProjectId)> edges)
    {
        var byProjectId = projects.ToDictionary(p => p.ProjectId);
        var consumersOf = new Dictionary<int, List<int>>();
        foreach (var (dependentId, referencedId) in edges)
        {
            if (!byProjectId.ContainsKey(dependentId) || !byProjectId.ContainsKey(referencedId))
                continue;
            if (!consumersOf.TryGetValue(referencedId, out var list))
            {
                list = new List<int>();
                consumersOf[referencedId] = list;
            }
            list.Add(dependentId);
        }

        var result = new Dictionary<int, IReadOnlyList<PackageDependentRepository>>();
        foreach (var package in projects)
        {
            if (string.IsNullOrWhiteSpace(package.PackageId))
                continue;

            var byRepo = new Dictionary<int, PackageDependentRepository>();
            var visited = new HashSet<int> { package.ProjectId };
            var queue = new Queue<(int ProjectId, string? Via)>();
            queue.Enqueue((package.ProjectId, null));
            while (queue.Count > 0)
            {
                var (projectId, via) = queue.Dequeue();
                if (!consumersOf.TryGetValue(projectId, out var consumers))
                    continue;
                foreach (var consumerId in consumers)
                {
                    var consumer = byProjectId[consumerId];
                    if (consumer.RepositoryId != package.RepositoryId
                        && (!byRepo.TryGetValue(consumer.RepositoryId, out var existing) || (!existing.IsDirect && via == null)))
                    {
                        byRepo[consumer.RepositoryId] = new PackageDependentRepository(consumer.RepositoryId, consumer.RepositoryName, via);
                    }
                    if (!string.IsNullOrWhiteSpace(consumer.PackageId) && visited.Add(consumerId))
                        queue.Enqueue((consumerId, consumer.PackageId!.Trim()));
                }
            }

            if (byRepo.Count > 0)
            {
                result[package.ProjectId] = byRepo.Values
                    .OrderBy(r => r.IsDirect ? 0 : 1)
                    .ThenBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Walks package edges out from every project in a repository. A project that references another repository's
    /// package makes that package a direct need; packages that package restores are needs through it, including the
    /// other packages that repository publishes. A package reached both ways is reported as direct. The same list is
    /// stored on every package project in the repository.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<PackageAccessNeed>> ResolvePackageAccessNeeds(
        IReadOnlyList<PackageDependentsNode> projects,
        IReadOnlyList<(int DependentProjectId, int ReferencedProjectId)> edges)
    {
        var byProjectId = projects.ToDictionary(p => p.ProjectId);
        var packagesByRepository = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageId))
            .GroupBy(p => p.RepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PackageDependentsNode>)g.ToList());
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (dependentId, referencedId) in edges)
        {
            if (!byProjectId.ContainsKey(dependentId) || !byProjectId.ContainsKey(referencedId))
                continue;
            if (!outgoing.TryGetValue(dependentId, out var list))
            {
                list = new List<int>();
                outgoing[dependentId] = list;
            }
            list.Add(referencedId);
        }

        var result = new Dictionary<int, IReadOnlyList<PackageAccessNeed>>();
        foreach (var repo in projects.GroupBy(p => p.RepositoryId))
        {
            var repoProjects = repo.ToList();
            var needs = CollectPackageAccessNeeds(repo.Key, repoProjects, byProjectId, packagesByRepository, outgoing);
            if (needs.Count == 0)
                continue;
            foreach (var project in repoProjects)
            {
                if (!string.IsNullOrWhiteSpace(project.PackageId))
                    result[project.ProjectId] = needs;
            }
        }

        return result;
    }

    private static List<PackageAccessNeed> CollectPackageAccessNeeds(
        int repositoryId,
        IReadOnlyList<PackageDependentsNode> repoProjects,
        Dictionary<int, PackageDependentsNode> byProjectId,
        Dictionary<int, IReadOnlyList<PackageDependentsNode>> packagesByRepository,
        Dictionary<int, List<int>> outgoing)
    {
        var byPackage = new Dictionary<string, PackageAccessNeed>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<int>(repoProjects.Select(p => p.ProjectId));
        var queue = new Queue<(int ProjectId, string? Via)>();
        foreach (var project in repoProjects)
            queue.Enqueue((project.ProjectId, null));

        while (queue.Count > 0)
        {
            var (projectId, via) = queue.Dequeue();
            if (!outgoing.TryGetValue(projectId, out var referencedIds))
                continue;
            foreach (var referencedId in referencedIds)
            {
                var referenced = byProjectId[referencedId];
                var packageId = referenced.PackageId?.Trim();
                if (referenced.RepositoryId == repositoryId || string.IsNullOrEmpty(packageId))
                    continue;

                RememberNeed(packageId, via);
                if (packagesByRepository.TryGetValue(referenced.RepositoryId, out var siblings))
                {
                    foreach (var sibling in siblings)
                    {
                        var siblingId = sibling.PackageId?.Trim();
                        if (string.IsNullOrEmpty(siblingId) || siblingId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        RememberNeed(siblingId, packageId);
                        if (visited.Add(sibling.ProjectId))
                            queue.Enqueue((sibling.ProjectId, siblingId));
                    }
                }

                if (visited.Add(referencedId))
                    queue.Enqueue((referencedId, packageId));
            }
        }

        void RememberNeed(string packageId, string? needVia)
        {
            if (!byPackage.TryGetValue(packageId, out var existing) || (!existing.IsDirect && needVia == null))
                byPackage[packageId] = new PackageAccessNeed(packageId, needVia);
        }

        return byPackage.Values
            .OrderBy(n => n.IsDirect ? 0 : 1)
            .ThenBy(n => n.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
