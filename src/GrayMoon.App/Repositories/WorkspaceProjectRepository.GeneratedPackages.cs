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

        var existingGenerated = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && p.IsGenerated)
            .ToListAsync(cancellationToken);

        var desiredGeneratedKeys = distinctResolved
            .Select(r => (r.ProducerRepositoryId, r.PackageName))
            .Distinct()
            .ToHashSet();

        var generatedByKey = existingGenerated
            .ToDictionary(p => (p.RepositoryId, PackageName: p.PackageId?.Trim() ?? ""), p => p);

        var toRemoveGenerated = existingGenerated
            .Where(p => !desiredGeneratedKeys.Contains((p.RepositoryId, p.PackageId?.Trim() ?? "")))
            .ToList();
        if (toRemoveGenerated.Count > 0)
        {
            dbContext.WorkspaceProjects.RemoveRange(toRemoveGenerated);
            logger.LogDebug("Persistence: WorkspaceProjects (generated). Action=Remove, WorkspaceId={WorkspaceId}, Count={Count}", workspaceId, toRemoveGenerated.Count);
        }

        foreach (var key in desiredGeneratedKeys)
        {
            if (generatedByKey.ContainsKey(key)) continue;
            var newProject = new WorkspaceProject
            {
                WorkspaceId = workspaceId,
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
        await dbContext.SaveChangesAsync(cancellationToken);

        var realProjects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && !p.IsGenerated)
            .ToListAsync(cancellationToken);
        var realByRepoAndPath = realProjects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectFilePath))
            .GroupBy(p => (p.RepositoryId, ProjectFilePath: NormalizeRepoRelativePath(p.ProjectFilePath)))
            .ToDictionary(g => g.Key, g => g.First());

        var desiredEdges = new Dictionary<(int DependentProjectId, int ReferencedProjectId), string?>();
        foreach (var r in distinctResolved)
        {
            if (!realByRepoAndPath.TryGetValue((r.ConsumerRepositoryId, NormalizeRepoRelativePath(r.ConsumerProjectFilePath)), out var consumerProject)) continue;
            if (!generatedByKey.TryGetValue((r.ProducerRepositoryId, r.PackageName), out var generatedProject)) continue;
            var version = string.IsNullOrWhiteSpace(r.Version) ? null : r.Version.Trim();
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
            .ToDictionary(e => (e.DependentProjectId, e.ReferencedProjectId));
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
