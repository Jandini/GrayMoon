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
    WorkspaceFileVersionService? fileVersionService = null)
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

    /// <summary>Recomputes without broadcasting, for callers that send their own follow-up notification.</summary>
    public async Task RecomputeAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        if (fileVersionService != null)
        {
            try
            {
                await fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, contextId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A file-version check needs the worker; losing it must not also lose the dependency recompute.
                logger.LogError(ex, "File version check failed for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
            }
        }

        await RecomputeDependencyStatsAsync(workspaceId, contextId, cancellationToken);
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
