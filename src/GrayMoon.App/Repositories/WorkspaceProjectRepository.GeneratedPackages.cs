using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>
    /// Syncs virtual/generated NuGet package dependencies (packages with no physical package-producing .csproj,
    /// inferred from a configured .csproj version file whose version pattern resolves to a producer repository).
    /// For each resolved pair, ensures a generated <see cref="WorkspaceProject"/> row exists for the producer
    /// (RepositoryId = producer, PackageId = package name, IsGenerated = true) and a <see cref="ProjectDependency"/>
    /// edge from the consumer's real project to it - so dependency-level computation, push-plan package waiting,
    /// and restore treat it exactly like a physical package reference. Removes generated rows/edges that no longer
    /// correspond to any entry in <paramref name="resolved"/> (e.g. a version config was removed or edited).
    /// Generated rows are workspace-global (owned by the special Workspace context) - Feature contexts must not
    /// carry their own copies; callers that seed Feature projections should skip <see cref="WorkspaceProject.IsGenerated"/>.
    /// Does not recompute dependency levels; callers should follow with <see cref="RecomputeAndPersistRepositoryDependencyStatsAsync"/>.
    /// </summary>
    public async Task SyncGeneratedPackageDependenciesAsync(
        int workspaceId,
        IReadOnlyList<GeneratedPackageDependencyInfo> resolved,
        CancellationToken cancellationToken = default)
    {
        var distinctResolved = resolved
            .Where(r => !string.IsNullOrWhiteSpace(r.PackageName)
                && !string.IsNullOrWhiteSpace(r.ConsumerProjectFilePath)
                && r.ConsumerRepositoryId != r.ProducerRepositoryId)
            .GroupBy(r => (r.ConsumerRepositoryId, ConsumerProjectFilePath: r.ConsumerProjectFilePath.Trim(), r.ProducerRepositoryId, PackageName: r.PackageName.Trim()))
            .Select(g =>
            {
                var first = g.First();
                var version = string.IsNullOrWhiteSpace(first.Version) ? null : first.Version.Trim();
                return new GeneratedPackageDependencyInfo(
                    g.Key.ConsumerRepositoryId,
                    g.Key.ConsumerProjectFilePath,
                    g.Key.ProducerRepositoryId,
                    g.Key.PackageName,
                    version);
            })
            .ToList();

        var specialContextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);

        var existingGenerated = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && p.IsGenerated)
            .ToListAsync(cancellationToken);

        // Feature-seed used to clone generated rows into Feature contexts. Those copies break the
        // workspace-global (RepositoryId, PackageId) key and must be removed so sync can proceed.
        var featureScopedGenerated = existingGenerated
            .Where(p => p.WorkspaceFeatureContextId != specialContextId)
            .ToList();
        if (featureScopedGenerated.Count > 0)
        {
            var orphanIds = featureScopedGenerated.Select(p => p.ProjectId).ToHashSet();
            var orphanEdges = await dbContext.ProjectDependencies
                .Where(d => orphanIds.Contains(d.DependentProjectId) || orphanIds.Contains(d.ReferencedProjectId))
                .ToListAsync(cancellationToken);
            if (orphanEdges.Count > 0)
                dbContext.ProjectDependencies.RemoveRange(orphanEdges);
            dbContext.WorkspaceProjects.RemoveRange(featureScopedGenerated);
            logger.LogInformation(
                "Persistence: WorkspaceProjects (generated). Action=RemoveFeatureScoped, WorkspaceId={WorkspaceId}, Count={Count}",
                workspaceId, featureScopedGenerated.Count);
            existingGenerated = existingGenerated
                .Where(p => p.WorkspaceFeatureContextId == specialContextId)
                .ToList();
        }

        var desiredGeneratedKeys = distinctResolved
            .Select(r => (r.ProducerRepositoryId, r.PackageName))
            .Distinct()
            .ToHashSet();

        // Deduplicate defensively: legacy/partial writes can leave multiple special-context rows for the same key.
        var generatedByKey = new Dictionary<(int RepositoryId, string PackageName), WorkspaceProject>();
        foreach (var group in existingGenerated.GroupBy(p => (p.RepositoryId, PackageName: p.PackageId?.Trim() ?? "")))
        {
            var keep = group.First();
            var extras = group.Skip(1).ToList();
            if (extras.Count > 0)
            {
                var extraIds = extras.Select(p => p.ProjectId).ToHashSet();
                var extraEdges = await dbContext.ProjectDependencies
                    .Where(d => extraIds.Contains(d.DependentProjectId) || extraIds.Contains(d.ReferencedProjectId))
                    .ToListAsync(cancellationToken);
                if (extraEdges.Count > 0)
                    dbContext.ProjectDependencies.RemoveRange(extraEdges);
                dbContext.WorkspaceProjects.RemoveRange(extras);
                logger.LogWarning(
                    "Persistence: WorkspaceProjects (generated). Action=Deduplicate, WorkspaceId={WorkspaceId}, Key={RepositoryId}/{PackageName}, Removed={Count}",
                    workspaceId, group.Key.RepositoryId, group.Key.PackageName, extras.Count);
            }
            generatedByKey[group.Key] = keep;
        }

        var toRemoveGenerated = generatedByKey
            .Where(kv => !desiredGeneratedKeys.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        if (toRemoveGenerated.Count > 0)
        {
            dbContext.WorkspaceProjects.RemoveRange(toRemoveGenerated);
            foreach (var p in toRemoveGenerated)
                generatedByKey.Remove((p.RepositoryId, p.PackageId?.Trim() ?? ""));
            logger.LogDebug("Persistence: WorkspaceProjects (generated). Action=Remove, WorkspaceId={WorkspaceId}, Count={Count}", workspaceId, toRemoveGenerated.Count);
        }

        foreach (var key in desiredGeneratedKeys)
        {
            if (generatedByKey.ContainsKey(key)) continue;
            var newProject = new WorkspaceProject
            {
                WorkspaceId = workspaceId,
                WorkspaceFeatureContextId = specialContextId,
                RepositoryId = key.ProducerRepositoryId,
                ProjectName = key.PackageName,
                ProjectType = ProjectType.Package,
                ProjectFilePath = "",
                TargetFramework = "",
                PackageId = key.PackageName,
                IsGenerated = true
            };
            dbContext.WorkspaceProjects.Add(newProject);
            generatedByKey[key] = newProject;
        }

        // Save now so newly-added generated projects get real ProjectIds before edges reference them.
        // Also flushes feature-scoped orphan removals / dedupe removals.
        await dbContext.SaveChangesAsync(cancellationToken);

        var realProjects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && !p.IsGenerated)
            .ToListAsync(cancellationToken);
        var realByRepoAndPath = realProjects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectFilePath))
            .GroupBy(p => (p.RepositoryId, ProjectFilePath: NormalizeRepoRelativePath(p.ProjectFilePath)))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WorkspaceProject>)g.ToList());

        var desiredEdges = new Dictionary<(int DependentProjectId, int ReferencedProjectId), string?>();
        foreach (var r in distinctResolved)
        {
            if (!realByRepoAndPath.TryGetValue((r.ConsumerRepositoryId, NormalizeRepoRelativePath(r.ConsumerProjectFilePath)), out var consumers)) continue;
            if (!generatedByKey.TryGetValue((r.ProducerRepositoryId, r.PackageName), out var generatedProject)) continue;
            var version = string.IsNullOrWhiteSpace(r.Version) ? null : r.Version.Trim();
            foreach (var consumerProject in consumers)
                desiredEdges[(consumerProject.ProjectId, generatedProject.ProjectId)] = version;
        }

        var generatedProjectIds = generatedByKey.Values.Select(p => p.ProjectId).ToHashSet();
        var existingGeneratedEdges = generatedProjectIds.Count == 0
            ? new List<ProjectDependency>()
            : await dbContext.ProjectDependencies
                .Where(d => generatedProjectIds.Contains(d.ReferencedProjectId))
                .ToListAsync(cancellationToken);

        var desiredEdgeKeys = desiredEdges.Keys.ToHashSet();
        var toRemoveEdges = existingGeneratedEdges
            .Where(e => !desiredEdgeKeys.Contains((e.DependentProjectId, e.ReferencedProjectId)))
            .ToList();
        if (toRemoveEdges.Count > 0)
            dbContext.ProjectDependencies.RemoveRange(toRemoveEdges);

        var existingByKey = existingGeneratedEdges
            .Where(e => desiredEdgeKeys.Contains((e.DependentProjectId, e.ReferencedProjectId)))
            .GroupBy(e => (e.DependentProjectId, e.ReferencedProjectId))
            .ToDictionary(g => g.Key, g => g.First());
        var addedEdges = 0;
        var updatedEdges = 0;
        foreach (var (key, version) in desiredEdges)
        {
            if (existingByKey.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Version, version, StringComparison.Ordinal))
                {
                    existing.Version = version;
                    updatedEdges++;
                }
                continue;
            }

            dbContext.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = key.DependentProjectId,
                ReferencedProjectId = key.ReferencedProjectId,
                Version = version
            });
            addedEdges++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Persistence: Generated package dependencies. WorkspaceId={WorkspaceId}, GeneratedPackages={PackageCount}, EdgesAdded={Added}, EdgesUpdated={Updated}, EdgesRemoved={Removed}",
            workspaceId, desiredGeneratedKeys.Count, addedEdges, updatedEdges, toRemoveEdges.Count);
    }

    /// <summary>Repo-relative path identity: trim, unify separators to '/', case-insensitive.</summary>
    private static string NormalizeRepoRelativePath(string path) =>
        path.Trim().Replace('\\', '/').ToLowerInvariant();
}
