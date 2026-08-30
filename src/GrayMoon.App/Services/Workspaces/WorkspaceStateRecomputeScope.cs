using GrayMoon.App.Hubs;
using GrayMoon.App.Repositories;
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
/// <c>RepositorySynced</c> is deliberately not coalesced here: it carries the repository id that
/// <c>WorkspaceActions</c> uses to target its GitHub Actions refresh, so it keeps firing per repository.
/// </para>
/// </remarks>
public sealed class WorkspaceStateRecomputeScope(
    WorkspaceProjectRepository workspaceProjectRepository,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<WorkspaceStateRecomputeScope> logger,
    WorkspaceFileVersionService? fileVersionService = null)
{
    /// <summary>Recomputes file-version and dependency stats for the whole workspace, then broadcasts <c>WorkspaceSynced</c> once.</summary>
    public async Task CompleteAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await RecomputeAsync(workspaceId, cancellationToken);
        await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
    }

    /// <summary>Recomputes without broadcasting, for callers that send their own follow-up notification.</summary>
    public async Task RecomputeAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (fileVersionService != null)
        {
            try
            {
                await fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A file-version check needs the agent; losing it must not also lose the dependency recompute.
                logger.LogError(ex, "File version check failed for workspace {WorkspaceId}", workspaceId);
            }
        }

        await workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);
    }
}
