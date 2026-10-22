using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed partial class WorkspaceProjectRepository
{
    /// <summary>Merges projects for a repository in a workspace Feature context by physical path (not name). Removes persisted projects not in <paramref name="projects"/>; adds new; updates existing.</summary>
    public async Task MergeWorkspaceProjectsAsync(
        int workspaceId,
        int repositoryId,
        IReadOnlyList<SyncProjectInfo> projects,
        CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await MergeWorkspaceProjectsAsync(workspaceId, repositoryId, projects, contextId, cancellationToken);
    }

    public async Task MergeWorkspaceProjectsAsync(
        int workspaceId,
        int repositoryId,
        IReadOnlyList<SyncProjectInfo> projects,
        int workspaceFeatureContextId,
        CancellationToken cancellationToken = default)
    {
        using var projectionGate = await AcquireProjectionGateAsync(workspaceFeatureContextId, cancellationToken);

        // Generated (virtual/inferred) package rows are owned by SyncGeneratedPackageDependenciesAsync, not by
        // this per-repo sync reconciliation - a real repo's own project scan must never delete a generated
        // package row it happens to "produce" (it has no physical .csproj producing it, so it never appears here).
        var existing =await dbContext.WorkspaceProjects
            .Where(p =>
                p.WorkspaceId == workspaceId
                && p.RepositoryId == repositoryId
                && p.WorkspaceFeatureContextId == workspaceFeatureContextId
                && !p.IsGenerated)
            .ToListAsync(cancellationToken);

        var (removed, addedOrUpdated) = MergeProjectsForRepository(workspaceId, repositoryId, workspaceFeatureContextId, projects, existing);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: WorkspaceProjects. Action=Merge, WorkspaceId={WorkspaceId}, ContextId={ContextId}, RepositoryId={RepositoryId}, Removed={Removed}, AddedOrUpdated={Count}",
            workspaceId, workspaceFeatureContextId, repositoryId, removed, addedOrUpdated);
    }

    /// <summary>
    /// Batched form of <see cref="MergeWorkspaceProjectsAsync"/> for multiple repos in one workspace: loads every
    /// repo's existing (non-generated) <see cref="WorkspaceProject"/> rows with a single query, merges each repo's
    /// projects in memory, and calls <see cref="AppDbContext.SaveChangesAsync"/> exactly once for the whole batch -
    /// instead of one query plus one save per repo in a caller-side loop.
    /// </summary>
    public async Task MergeWorkspaceProjectsBatchAsync(
        int workspaceId,
        IReadOnlyList<(int RepositoryId, IReadOnlyList<SyncProjectInfo> Projects)> repoProjects,
        CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await MergeWorkspaceProjectsBatchAsync(workspaceId, repoProjects, contextId, cancellationToken);
    }

    public async Task MergeWorkspaceProjectsBatchAsync(
        int workspaceId,
        IReadOnlyList<(int RepositoryId, IReadOnlyList<SyncProjectInfo> Projects)> repoProjects,
        int workspaceFeatureContextId,
        CancellationToken cancellationToken = default)
    {
        if (repoProjects == null || repoProjects.Count == 0) return;

        using var projectionGate = await AcquireProjectionGateAsync(workspaceFeatureContextId, cancellationToken);
        await MergeProjectsBatchCoreAsync(workspaceId, repoProjects, workspaceFeatureContextId, cancellationToken);
    }

    /// <summary>Batch project merge without the projection gate (the caller holds it). Saves once.</summary>
    private async Task MergeProjectsBatchCoreAsync(
        int workspaceId,
        IReadOnlyList<(int RepositoryId, IReadOnlyList<SyncProjectInfo> Projects)> repoProjects,
        int workspaceFeatureContextId,
        CancellationToken cancellationToken = default)
    {
        var repoIds = repoProjects.Select(r => r.RepositoryId).ToHashSet();
        var existingAll = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId
                        && p.WorkspaceFeatureContextId == workspaceFeatureContextId
                        && repoIds.Contains(p.RepositoryId)
                        && !p.IsGenerated)
            .ToListAsync(cancellationToken);
        var existingByRepo = existingAll.ToLookup(p => p.RepositoryId);

        var totalRemoved = 0;
        var totalAddedOrUpdated = 0;
        foreach (var (repositoryId, projects) in repoProjects)
        {
            var (removed, addedOrUpdated) = MergeProjectsForRepository(
                workspaceId, repositoryId, workspaceFeatureContextId, projects, existingByRepo[repositoryId].ToList());
            totalRemoved += removed;
            totalAddedOrUpdated += addedOrUpdated;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: WorkspaceProjects. Action=MergeBatch, WorkspaceId={WorkspaceId}, ContextId={ContextId}, RepoCount={RepoCount}, Removed={Removed}, AddedOrUpdated={Count}",
            workspaceId, workspaceFeatureContextId, repoProjects.Count, totalRemoved, totalAddedOrUpdated);
    }

    /// <summary>
    /// Reconciles one repo's incoming projects against its already-loaded existing rows by physical identity
    /// (<see cref="WorkspaceProject.ProjectIdentityKey"/>, never the project name): stages Remove/Add on <c>dbContext</c> and
    /// mutates tracked entities in place. A persisted row whose own path is unusable (legacy) is adopted by a same-name
    /// incoming project that no other row claims, so its ProjectId survives. Caller saves and decides whether the
    /// incoming list is authoritative (a successful empty scan removes every physical row). Returns (Removed, AddedOrUpdated) for logging.
    /// </summary>
    private (int Removed, int AddedOrUpdated) MergeProjectsForRepository(
        int workspaceId,
        int repositoryId,
        int workspaceFeatureContextId,
        IReadOnlyList<SyncProjectInfo> projects,
        IReadOnlyList<WorkspaceProject> existing)
    {
        var incoming = IndexIncomingProjects(projects, workspaceFeatureContextId, repositoryId, logConflicts: true);
        LogDuplicateProjectNames(workspaceId, repositoryId, workspaceFeatureContextId, incoming.Values);

        var matched = new Dictionary<string, WorkspaceProject>(StringComparer.Ordinal);
        var unmatched = new List<WorkspaceProject>();
        foreach (var p in existing.OrderBy(p => p.ProjectId))
        {
            var key = p.ProjectIdentityKey ?? ProjectIdentity.PhysicalKey(p.ProjectFilePath);
            if (key is not null && incoming.ContainsKey(key) && matched.TryAdd(key, p))
                continue;
            unmatched.Add(p);
        }

        // Legacy rows with no usable path of their own: adopt a same-name incoming project nobody else claims.
        var unclaimed = incoming.Where(kv => !matched.ContainsKey(kv.Key)).ToList();
        var adopted = new HashSet<string>(StringComparer.Ordinal);
        var toRemove = new List<WorkspaceProject>();
        foreach (var p in unmatched)
        {
            var hasOwnKey = (p.ProjectIdentityKey ?? ProjectIdentity.PhysicalKey(p.ProjectFilePath)) is not null;
            var candidate = hasOwnKey
                ? default
                : unclaimed.FirstOrDefault(kv => !adopted.Contains(kv.Key)
                    && string.Equals(kv.Value.ProjectName.Trim(), p.ProjectName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (candidate.Value is not null)
            {
                adopted.Add(candidate.Key);
                matched[candidate.Key] = p;
            }
            else
            {
                toRemove.Add(p);
            }
        }

        if (toRemove.Count > 0)
        {
            dbContext.WorkspaceProjects.RemoveRange(toRemove);
            logger.LogDebug("WorkspaceProjects merge: WorkspaceId={WorkspaceId}, ContextId={ContextId}, RepositoryId={RepositoryId}, removed {Count} no longer present by path", workspaceId, workspaceFeatureContextId, repositoryId, toRemove.Count);
        }

        foreach (var (key, info) in incoming)
        {
            if (matched.TryGetValue(key, out var row))
            {
                row.ProjectName = info.ProjectName.Trim();
                row.ProjectType = info.ProjectType;
                row.ProjectFilePath = info.ProjectFilePath;
                row.TargetFramework = info.TargetFramework;
                row.PackageId = string.IsNullOrWhiteSpace(info.PackageId) ? null : info.PackageId;
                row.WorkspaceFeatureContextId = workspaceFeatureContextId;
                row.RefreshIdentityKey();
                continue;
            }

            var added = new WorkspaceProject
            {
                WorkspaceId = workspaceId,
                WorkspaceFeatureContextId = workspaceFeatureContextId,
                RepositoryId = repositoryId,
                ProjectName = info.ProjectName.Trim(),
                ProjectType = info.ProjectType,
                ProjectFilePath = info.ProjectFilePath,
                TargetFramework = info.TargetFramework,
                PackageId = string.IsNullOrWhiteSpace(info.PackageId) ? null : info.PackageId
            };
            added.RefreshIdentityKey();
            dbContext.WorkspaceProjects.Add(added);
        }

        return (toRemove.Count, incoming.Count);
    }

    private const int DiagnosticSampleSize = 5;

    /// <summary>
    /// Same-named projects at different paths are legitimate (each is its own project), so this is a diagnostic, not an
    /// error: one bounded warning per repository per scan, nothing when every name is unique. Works on the in-memory scan only.
    /// </summary>
    private void LogDuplicateProjectNames(int workspaceId, int repositoryId, int workspaceFeatureContextId, IEnumerable<SyncProjectInfo> incoming)
    {
        var duplicates = incoming
            .GroupBy(p => p.ProjectName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count == 0)
            return;

        var sample = string.Join("; ", duplicates.Take(DiagnosticSampleSize).Select(g =>
            $"{g.Key} x{g.Count()} [{string.Join(", ", g.Take(DiagnosticSampleSize).Select(p => p.ProjectFilePath))}]"));
        logger.LogWarning(
            "Project identity diagnostic: Category=DuplicateProjectName, WorkspaceId={WorkspaceId}, ContextId={ContextId}, RepositoryId={RepositoryId}, DuplicateNames={Count}, Sample={Sample}, Resolution=KeptDistinctByPath",
            workspaceId, workspaceFeatureContextId, repositoryId, duplicates.Count, sample);
    }

    /// <summary>
    /// Indexes one repo's incoming projects by physical identity key. Entries with a blank name or an unusable path are
    /// skipped and logged; two entries for the same path are one project when identical, otherwise the first wins and the
    /// conflict is logged. Same-named projects at different paths are all kept.
    /// </summary>
    private Dictionary<string, SyncProjectInfo> IndexIncomingProjects(
        IEnumerable<SyncProjectInfo> projects,
        int workspaceFeatureContextId,
        int repositoryId,
        bool logConflicts)
    {
        var byKey = new Dictionary<string, SyncProjectInfo>(StringComparer.Ordinal);
        foreach (var info in projects)
        {
            if (string.IsNullOrWhiteSpace(info.ProjectName))
                continue;

            var key = ProjectIdentity.PhysicalKey(info.ProjectFilePath);
            if (key is null)
            {
                if (logConflicts)
                    logger.LogWarning("WorkspaceProjects merge: ContextId={ContextId}, RepositoryId={RepositoryId}, project '{ProjectName}' has an unusable path '{Path}' and was skipped",
                        workspaceFeatureContextId, repositoryId, info.ProjectName, info.ProjectFilePath);
                continue;
            }

            if (!byKey.TryGetValue(key, out var first))
            {
                byKey[key] = info;
                continue;
            }

            var identical = string.Equals(first.ProjectName.Trim(), info.ProjectName.Trim(), StringComparison.OrdinalIgnoreCase)
                && first.ProjectType == info.ProjectType
                && first.TargetFramework == info.TargetFramework
                && string.Equals(first.PackageId?.Trim(), info.PackageId?.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!identical && logConflicts)
                logger.LogWarning("WorkspaceProjects merge: ContextId={ContextId}, RepositoryId={RepositoryId}, conflicting entries for path '{Path}' ('{First}' vs '{Second}'); keeping the first",
                    workspaceFeatureContextId, repositoryId, key, first.ProjectName, info.ProjectName);
        }

        return byKey;
    }

    /// <summary>Serializes project/edge writers of one context with the Feature seed (see <see cref="GrayMoon.App.Services.Features.FeatureFinalizationCoordinator"/>). No-op without a coordinator.</summary>
    private async Task<IDisposable> AcquireProjectionGateAsync(int workspaceFeatureContextId, CancellationToken cancellationToken) =>
        finalizationCoordinator is null
            ? NoopScope.Instance
            : await finalizationCoordinator.AcquireProjectionAsync(workspaceFeatureContextId, cancellationToken);

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private async Task<int> ResolveSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace)
            .Select(c => c.WorkspaceFeatureContextId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing != 0)
            return existing;

        var workspace = await dbContext.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found.");

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Workspace,
            CreatedAt = DateTime.UtcNow,
            LastSyncedAt = workspace.LastSyncedAt,
            IsInSync = workspace.IsInSync
        };
        dbContext.WorkspaceFeatureContexts.Add(context);
        await dbContext.SaveChangesAsync(cancellationToken);
        return context.WorkspaceFeatureContextId;
    }

    /// <summary>Replaces project dependencies for workspace projects from sync results. Dependents are resolved by physical path and referenced packages by unambiguous effective PackageId; a package published by several projects creates no edge. Edges pointing at a project that is no longer a unique producer are removed. When <paramref name="persistDependencyLevel"/> is true, levels are recomputed from the full DB graph (not the partial merge batch). When false, callers such as <c>WorkspaceGitService.PersistVersionsAsync</c> follow with <c>RecomputeAndPersistRepositoryDependencyStatsAsync</c>.</summary>
    public async Task MergeWorkspaceProjectDependenciesAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, contextId, persistDependencyLevel, cancellationToken);
    }

    public async Task MergeWorkspaceProjectDependenciesAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
        int workspaceFeatureContextId,
        bool persistDependencyLevel = true,
        CancellationToken cancellationToken = default)
    {
        using var projectionGate = await AcquireProjectionGateAsync(workspaceFeatureContextId, cancellationToken);
        await MergeDependenciesCoreAsync(workspaceId, syncResults, workspaceFeatureContextId, persistDependencyLevel, cancellationToken);
    }

    /// <summary>Dependency merge without the projection gate (the caller holds it).</summary>
    private async Task MergeDependenciesCoreAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, IReadOnlyList<SyncProjectInfo>? ProjectsDetail)> syncResults,
        int workspaceFeatureContextId,
        bool persistDependencyLevel,
        CancellationToken cancellationToken)
    {
        var workspaceProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .ToListAsync(cancellationToken);
        if (workspaceProjects.Count == 0) return;

        var targets = PackageTargetIndex.Build(workspaceProjects);

        var dependentProjectIds = new HashSet<int>();
        var edges = new List<(int DependentProjectId, int ReferencedProjectId, string? Version)>();
        var ambiguousConsumers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoId, projectsDetail) in syncResults)
        {
            if (projectsDetail == null || projectsDetail.Count == 0) continue;

            var repoProjects = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in workspaceProjects.Where(p => p.RepositoryId == repoId && !p.IsGenerated).OrderBy(p => p.ProjectId))
            {
                var key = p.ProjectIdentityKey ?? ProjectIdentity.PhysicalKey(p.ProjectFilePath);
                if (key is not null)
                    repoProjects.TryAdd(key, p.ProjectId);
            }

            // Each dependent is the project at that physical path, so same-named projects keep their own PackageReferences.
            foreach (var (key, info) in IndexIncomingProjects(projectsDetail, workspaceFeatureContextId, repoId, logConflicts: false))
            {
                if (!repoProjects.TryGetValue(key, out var dependentProjectId)) continue;

                dependentProjectIds.Add(dependentProjectId);

                foreach (var pr in info.PackageReferences)
                {
                    if (string.IsNullOrWhiteSpace(pr.Name)) continue;
                    var resolution = targets.Resolve(pr.Name);
                    if (resolution.IsAmbiguous)
                    {
                        ambiguousConsumers[pr.Name.Trim()] = ambiguousConsumers.GetValueOrDefault(pr.Name.Trim()) + 1;
                        continue;
                    }
                    if (resolution.ProjectId is not { } referencedProjectId) continue;
                    if (referencedProjectId == dependentProjectId) continue;
                    var version = string.IsNullOrWhiteSpace(pr.Version) ? null : pr.Version.Trim();
                    edges.Add((dependentProjectId, referencedProjectId, version));
                }
            }
        }

        foreach (var (packageId, consumers) in ambiguousConsumers)
            logger.LogWarning(
                "Project identity diagnostic: Category=AmbiguousPackageProducer, WorkspaceId={WorkspaceId}, ContextId={ContextId}, PackageId={PackageId}, ProducerCount={ProducerCount}, Producers={Producers}, ConsumerReferences={Consumers}, Resolution=NoEdge",
                workspaceId, workspaceFeatureContextId, packageId, targets.ProducerCount(packageId), targets.DescribeProducers(packageId), consumers);

        // An edge is valid only while its referenced project is the unique producer of a package. Producers can lose
        // that status (PackageId removed or changed, project removed, a second producer appeared) in a scan that does
        // not include their consumers, so such edges are dropped here; their consumers are re-resolved on their next scan.
        var staleEdges = (await dbContext.ProjectDependencies
                .Where(d => d.DependentProject!.WorkspaceId == workspaceId
                            && d.DependentProject.WorkspaceFeatureContextId == workspaceFeatureContextId)
                .ToListAsync(cancellationToken))
            .Where(d => !dependentProjectIds.Contains(d.DependentProjectId) && !targets.UniqueTargetIds.Contains(d.ReferencedProjectId))
            .ToList();
        if (staleEdges.Count > 0)
        {
            dbContext.ProjectDependencies.RemoveRange(staleEdges);
            logger.LogWarning(
                "Persistence: ProjectDependencies. WorkspaceId={WorkspaceId}, ContextId={ContextId}, Action=InvalidateStale, Edges={Edges}; dependents need a rescan to re-resolve",
                workspaceId, workspaceFeatureContextId, staleEdges.Count);
        }

        if (dependentProjectIds.Count == 0)
        {
            if (staleEdges.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                if (persistDependencyLevel)
                    await RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
            }
            return;
        }

        var existing = await dbContext.ProjectDependencies
            .Where(d => dependentProjectIds.Contains(d.DependentProjectId))
            .ToListAsync(cancellationToken);
        dbContext.ProjectDependencies.RemoveRange(existing);

        var uniqueEdges = edges
            .GroupBy(e => (e.DependentProjectId, e.ReferencedProjectId))
            .Select(g => (g.Key.DependentProjectId, g.Key.ReferencedProjectId, g.First().Version))
            .ToList();
        foreach (var (depId, refId, version) in uniqueEdges)
        {
            dbContext.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = depId,
                ReferencedProjectId = refId,
                Version = version
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Persistence: ProjectDependencies. WorkspaceId={WorkspaceId}, ContextId={ContextId}, DependentCount={Count}, EdgeCount={Edges}",
            workspaceId, workspaceFeatureContextId, dependentProjectIds.Count, uniqueEdges.Count);

        if (persistDependencyLevel)
            await RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
    }

    /// <summary>Legacy overload for callers without a context id: resolves the special Workspace context.</summary>
    public async Task UpdateProjectDependencyVersionsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string ProjectPath, string PackageId, string NewVersion)> updates,
        CancellationToken cancellationToken = default)
    {
        var contextId = await ResolveSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await UpdateProjectDependencyVersionsAsync(workspaceId, updates, contextId, cancellationToken);
    }

    /// <summary>Persists the new Version for ProjectDependencies that were updated by sync dependencies, scoped to <paramref name="workspaceFeatureContextId"/>. Matches by (ContextId, RepoId, ProjectPath) -> DependentProjectId and PackageId -> ReferencedProjectId, so a Feature and the Workspace (or another Feature) never collide on a project at the same relative path in the same repository.</summary>
    public async Task UpdateProjectDependencyVersionsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string ProjectPath, string PackageId, string NewVersion)> updates,
        int workspaceFeatureContextId,
        CancellationToken cancellationToken = default)
    {
        if (updates == null || updates.Count == 0) return;

        var projects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId && p.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0) return;

        var dependentKeyToProjectId = projects
            .Where(p => !p.IsGenerated && ProjectIdentity.PhysicalKey(p.ProjectFilePath) is not null)
            .GroupBy(p => (p.RepositoryId, ProjectPath: ProjectIdentity.PhysicalKey(p.ProjectFilePath)!))
            .ToDictionary(g => g.Key, g => g.First().ProjectId);

        var packageTargets = PackageTargetIndex.Build(projects);

        var projectIds = projects.Select(p => p.ProjectId).ToHashSet();
        var depRows = await dbContext.ProjectDependencies
            .Where(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId))
            .ToListAsync(cancellationToken);
        var byDepRef = depRows.ToLookup(d => (d.DependentProjectId, d.ReferencedProjectId));

        var updated = 0;
        foreach (var (repoId, projectPath, packageId, newVersion) in updates)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(newVersion))
                continue;

            if (ProjectIdentity.PhysicalKey(projectPath) is not { } pathKey)
                continue;
            var depKey = (repoId, pathKey);
            if (!dependentKeyToProjectId.TryGetValue(depKey, out var dependentProjectId))
                continue;
            if (packageTargets.Resolve(packageId).ProjectId is not { } referencedProjectId)
                continue;

            var key = (dependentProjectId, referencedProjectId);
            foreach (var row in byDepRef[key])
            {
                if (row.Version != newVersion)
                {
                    row.Version = newVersion;
                    updated++;
                }
            }
        }

        if (updated > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug("UpdateProjectDependencyVersions: WorkspaceId={WorkspaceId}, Updated={Count}", workspaceId, updated);
    }

    /// <summary>
    /// Package references match a workspace package only by effective <see cref="WorkspaceProject.PackageId"/>.
    /// Project name is not a fallback: a project that stopped being packable keeps its name and must not stay a target.
    /// A physical producer wins over a generated (inferred) row for the same id, because a generated row only exists
    /// where no physical package-producing project does; several producers of the same kind are ambiguous and never
    /// resolved by order.
    /// </summary>
    private sealed class PackageTargetIndex
    {
        public readonly record struct Resolution(int? ProjectId, bool IsAmbiguous);

        private readonly Dictionary<string, List<WorkspaceProject>> _chosen;

        private PackageTargetIndex(Dictionary<string, List<WorkspaceProject>> chosen)
        {
            _chosen = chosen;
            UniqueTargetIds = chosen.Values.Where(l => l.Count == 1).Select(l => l[0].ProjectId).ToHashSet();
        }

        /// <summary>Ids of projects that are the single producer of their package.</summary>
        public HashSet<int> UniqueTargetIds { get; }

        public static PackageTargetIndex Build(IEnumerable<WorkspaceProject> projects)
        {
            var groups = new Dictionary<string, List<WorkspaceProject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects.OrderBy(p => p.ProjectId))
            {
                var key = project.PackageId?.Trim();
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(project);
            }

            var chosen = new Dictionary<string, List<WorkspaceProject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, list) in groups)
            {
                var physical = list.Where(p => !p.IsGenerated).ToList();
                chosen[key] = physical.Count > 0 ? physical : list;
            }

            return new PackageTargetIndex(chosen);
        }

        public Resolution Resolve(string packageId)
        {
            if (!_chosen.TryGetValue(packageId.Trim(), out var list))
                return new Resolution(null, false);
            return list.Count == 1 ? new Resolution(list[0].ProjectId, false) : new Resolution(null, true);
        }

        public int ProducerCount(string packageId) => _chosen.TryGetValue(packageId.Trim(), out var list) ? list.Count : 0;

        public string DescribeProducers(string packageId) =>
            _chosen.TryGetValue(packageId.Trim(), out var list)
                ? string.Join(", ", list.Take(5).Select(p => $"{p.RepositoryId}:{(p.IsGenerated ? "(generated)" : p.ProjectFilePath)}")) + (list.Count > 5 ? $" (+{list.Count - 5})" : "")
                : string.Empty;
    }
}
