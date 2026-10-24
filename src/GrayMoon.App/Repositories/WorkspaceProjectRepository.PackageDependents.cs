using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>
    /// Returns, per package project (keyed by ProjectId), the other workspace repositories that restore it, directly or
    /// transitively through other workspace packages, scoped to a single context (null reproduces the legacy, unscoped
    /// behavior). The package's own repository is never listed.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PackageDependentRepository>>> GetPackageDependentRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken = default)
    {
        var scopedProjects = await GetByWorkspaceIdAsync(workspaceId, contextId, cancellationToken);
        if (scopedProjects.Count == 0)
            return new Dictionary<int, IReadOnlyList<PackageDependentRepository>>();

        var projectIdSet = scopedProjects.Select(p => p.ProjectId).ToHashSet();
        var edges = await dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIdSet.Contains(d.DependentProjectId) && projectIdSet.Contains(d.ReferencedProjectId))
            .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
            .ToListAsync(cancellationToken);

        var nodes = scopedProjects
            .Select(p => new PackageDependentsNode(p.ProjectId, p.RepositoryId, p.Repository?.RepositoryName ?? string.Empty, p.PackageId))
            .ToList();
        return ResolvePackageDependentRepositories(nodes, edges.Select(e => (e.DependentProjectId, e.ReferencedProjectId)).ToList());
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
}
