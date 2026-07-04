using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class WorkspaceRepositoryCustomDependencyRepository(
    AppDbContext dbContext,
    ILogger<WorkspaceRepositoryCustomDependencyRepository> logger)
{
    /// <summary>Returns custom dependency repo edges as (dependentRepositoryId, referencedRepositoryId) pairs.</summary>
    public async Task<HashSet<(int DepRepoId, int RefRepoId)>> GetRepoEdgesByWorkspaceIdAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceRepositoryCustomDependencies
            .AsNoTracking()
            .Include(d => d.DependentWorkspaceRepository)
            .Include(d => d.ReferencedWorkspaceRepository)
            .Where(d => d.DependentWorkspaceRepository!.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var edges = new HashSet<(int, int)>();
        foreach (var row in rows)
        {
            var depRepoId = row.DependentWorkspaceRepository?.RepositoryId;
            var refRepoId = row.ReferencedWorkspaceRepository?.RepositoryId;
            if (!depRepoId.HasValue || !refRepoId.HasValue) continue;
            if (depRepoId.Value == refRepoId.Value) continue;
            edges.Add((depRepoId.Value, refRepoId.Value));
        }

        return edges;
    }

    /// <summary>Returns user-selected custom referenced repository IDs for the given dependent repository.</summary>
    public async Task<HashSet<int>> GetCustomReferencedRepositoryIdsAsync(
        int workspaceId,
        int dependentRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var dependentLinkId = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == dependentRepositoryId)
            .Select(wr => (int?)wr.WorkspaceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!dependentLinkId.HasValue)
            return [];

        var referencedRepoIds = await dbContext.WorkspaceRepositoryCustomDependencies
            .AsNoTracking()
            .Where(d => d.DependentWorkspaceRepositoryId == dependentLinkId.Value)
            .Select(d => d.ReferencedWorkspaceRepository!.RepositoryId)
            .ToListAsync(cancellationToken);

        return referencedRepoIds.ToHashSet();
    }

    /// <summary>Custom dependency repo names for a single dependent repository (badge tooltip).</summary>
    public async Task<IReadOnlyList<string>> GetCustomDependencyNamesForRepoAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        var names = await dbContext.WorkspaceRepositoryCustomDependencies
            .AsNoTracking()
            .Where(d => d.DependentWorkspaceRepository!.WorkspaceId == workspaceId
                && d.DependentWorkspaceRepository.RepositoryId == repositoryId
                && d.ReferencedWorkspaceRepository!.Repository != null)
            .Select(d => d.ReferencedWorkspaceRepository!.Repository!.RepositoryName)
            .Where(n => n != null && n != string.Empty)
            .ToListAsync(cancellationToken);

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>Returns custom dependency repo names keyed by dependent repository ID (for badge tooltips).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetCustomDependencyNamesByRepoAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceRepositoryCustomDependencies
            .AsNoTracking()
            .Include(d => d.DependentWorkspaceRepository)
            .Include(d => d.ReferencedWorkspaceRepository)
                .ThenInclude(wr => wr!.Repository)
            .Where(d => d.DependentWorkspaceRepository!.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var dict = new Dictionary<int, List<string>>();
        foreach (var row in rows)
        {
            var depRepoId = row.DependentWorkspaceRepository?.RepositoryId;
            var refName = row.ReferencedWorkspaceRepository?.Repository?.RepositoryName;
            if (!depRepoId.HasValue || string.IsNullOrWhiteSpace(refName)) continue;

            if (!dict.TryGetValue(depRepoId.Value, out var names))
            {
                names = [];
                dict[depRepoId.Value] = names;
            }

            names.Add(refName);
        }

        foreach (var key in dict.Keys.ToList())
            dict[key] = dict[key].OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        return dict.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>Replaces user-selected custom dependencies for a dependent repository. Only stores custom edges, not implicit csproj/file refs.</summary>
    public async Task ReplaceCustomDependenciesForDependentAsync(
        int workspaceId,
        int dependentRepositoryId,
        IReadOnlySet<int> referencedRepositoryIds,
        CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId })
            .ToListAsync(cancellationToken);

        var linkIdByRepoId = links.ToDictionary(l => l.RepositoryId, l => l.WorkspaceRepositoryId);
        if (!linkIdByRepoId.TryGetValue(dependentRepositoryId, out var dependentLinkId))
            throw new InvalidOperationException("Dependent repository is not in the workspace.");

        var validReferencedLinkIds = new List<int>();
        foreach (var refRepoId in referencedRepositoryIds)
        {
            if (refRepoId == dependentRepositoryId) continue;
            if (!linkIdByRepoId.TryGetValue(refRepoId, out var refLinkId))
                throw new InvalidOperationException($"Referenced repository {refRepoId} is not in the workspace.");
            validReferencedLinkIds.Add(refLinkId);
        }

        var existing = await dbContext.WorkspaceRepositoryCustomDependencies
            .Where(d => d.DependentWorkspaceRepositoryId == dependentLinkId)
            .ToListAsync(cancellationToken);

        dbContext.WorkspaceRepositoryCustomDependencies.RemoveRange(existing);

        foreach (var refLinkId in validReferencedLinkIds.Distinct())
        {
            dbContext.WorkspaceRepositoryCustomDependencies.Add(new WorkspaceRepositoryCustomDependency
            {
                DependentWorkspaceRepositoryId = dependentLinkId,
                ReferencedWorkspaceRepositoryId = refLinkId
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Replaced custom dependencies for workspace {WorkspaceId} dependent repo {DependentRepositoryId}: {Count} edge(s).",
            workspaceId, dependentRepositoryId, validReferencedLinkIds.Distinct().Count());
    }
}
