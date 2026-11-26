using GrayMoon.App.Hubs;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// The batch boundary for a user action. Individual repository writes go through
/// <see cref="WorkspaceRepositoryStateWriter"/>; this runs the workspace-wide work that must happen
/// exactly once afterwards - the file-version check, the dependency-stat recompute, and the single
/// <c>WorkspaceSynced</c> broadcast that tells every browser to refresh.
/// </summary>
/// <remarks>
/// Both recomputes read and rewrite every repository's stats from a full workspace snapshot, so
/// running them per repository inside a parallel loop means N concurrent read-then-overwrite passes
/// racing on which snapshot's write lands last. Callers that touch several repositories must call
/// <see cref="CompleteAsync"/> once, after all of them have finished.
/// <para>
/// This is also the capability boundary for dependency state. The file-version check runs for every
/// workspace type; the dependency-stat recompute runs only when the workspace
/// <see cref="WorkspaceCapabilities.UsesDependencyGraph"/>, so a Basic workspace never gains dependency
/// levels or unmatched-dependency counts. <see cref="WorkspaceProjectRepository"/> stays type-agnostic.
/// </para>
/// <para>
/// <c>RepositorySynced</c> is deliberately not coalesced here: it carries the repository id that
/// <c>WorkspaceActions</c> uses to target its GitHub Actions refresh, so it keeps firing per repository.
/// </para>
/// </remarks>
public sealed class WorkspaceStateRecomputeScope(
    WorkspaceProjectRepository workspaceProjectRepository,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<WorkspaceStateRecomputeScope> logger,
    WorkspaceFileVersionService? fileVersionService = null,
    GrayMoon.App.Services.Versions.EffectiveVersionResolver? effectiveVersionResolver = null)
{
    /// <summary>Recomputes file-version and dependency stats for the given Feature context, then broadcasts sync once.</summary>
    public async Task CompleteAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        await RecomputeAsync(workspaceId, contextId, cancellationToken);
        await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value, cancellationToken);
        await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
    }

    /// <summary>
    /// The groups of a repository's stored state that the recompute reads. Effective versions are decided from the
    /// checked-out commit, the branch and the GitVersion result and from the projects that consume a version; the
    /// file-version check compares against versions and commits and reads files that only a new commit changes; the
    /// dependency stats read projects, edges and versions. Commit counts, the upstream flag and the branch and tag
    /// lists are read by none of the three.
    /// </summary>
    public const RepositoryFactGroups RecomputeInputs =
        RepositoryFactGroups.Identity | RepositoryFactGroups.GitVersion | RepositoryFactGroups.Projects;

    /// <summary>
    /// Whether a state write that changed <paramref name="changed"/> needs the recompute of its context. Null means
    /// the writer did not say what changed, which is treated as "everything".
    /// </summary>
    public static bool NeedsRecompute(RepositoryFactGroups? changed)
        => changed is not { } groups || (groups & RecomputeInputs) != 0;

    /// <summary>
    /// The full recompute of a context, without broadcasting, for callers that send their own follow-up notification.
    /// It rebuilds every derived value from the stored rows, so it is also the reference a narrower decision is
    /// checked against. Returns false when one of its parts failed (the failure is logged and the other parts still
    /// ran): the context is then owed another recompute.
    /// </summary>
    public async Task<bool> RecomputeAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        // Effective versions first (design D5): the file-version check and the dependency stats below compare against them.
        var complete = await ResolveEffectiveVersionsAsync(workspaceId, contextId, cancellationToken);

        if (fileVersionService != null)
        {
            try
            {
                await fileVersionService.CheckAndPersistFileVersionStatusAsync(
                    workspaceId, contextId, cancellationToken, deferDependencyRecompute: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A file-version check needs the worker; losing it must not also lose the dependency recompute.
                logger.LogError(ex, "File version check failed for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
                complete = false;
            }
        }

        await RecomputeDependencyStatsAsync(workspaceId, contextId, cancellationToken);
        return complete;
    }

    /// <summary>
    /// Resolves the effective versions of every repository of the context after its state was written (cached registry
    /// answers; a repository that left its branch point costs no Worker or registry call). A failure is logged and the
    /// stored decisions stay as they were: a stale inheritance is ignored at read once HEAD moves (I10), and every
    /// operation start resolves again with fresh answers.
    /// Returns false when the resolution failed.
    /// </summary>
    public async Task<bool> ResolveEffectiveVersionsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        if (effectiveVersionResolver == null)
            return true;

        try
        {
            await effectiveVersionResolver.ResolveAsync(workspaceId, contextId, repositoryIds: null, bypassCache: false, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Effective version resolution failed for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
            return false;
        }
    }

    /// <summary>
    /// Recomputes dependency levels, dependency counts and unmatched-dependency counts for the context, but
    /// only for a workspace that uses the dependency graph. Returns whether the recompute ran. Use this rather
    /// than calling <see cref="WorkspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(int, int, CancellationToken)"/>
    /// directly, so every orchestration path shares the one gate.
    /// </summary>
    public async Task<bool> RecomputeDependencyStatsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);
        if (!capabilities.UsesDependencyGraph)
            return false;

        await workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, contextId.Value, cancellationToken);
        return true;
    }
}
