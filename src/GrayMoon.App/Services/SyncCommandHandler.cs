using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>Handles SyncCommand from the agent (hook flow): persist version/branch/commit counts and upstream flag, recompute dependency stats for the workspace, then broadcast WorkspaceSynced so the grid can refresh.</summary>
public sealed class SyncCommandHandler(
    IServiceScopeFactory scopeFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<SyncCommandHandler> logger)
{
    public async Task HandleAsync(RepositorySyncNotification n)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workspaceProjectRepository = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == n.WorkspaceId && wr.RepositoryId == n.RepositoryId);
        if (wr == null)
        {
            logger.LogWarning("SyncCommand: workspace {WorkspaceId} repo {RepositoryId} not found", n.WorkspaceId, n.RepositoryId);
            return;
        }

        wr.GitVersion = n.Version == "-" ? null : n.Version;
        wr.BranchName = n.Branch == "-" ? null : n.Branch;
        if (n.OutgoingCommits.HasValue) wr.OutgoingCommits = n.OutgoingCommits;
        if (n.IncomingCommits.HasValue) wr.IncomingCommits = n.IncomingCommits;
        if (n.HasUpstream.HasValue) wr.BranchHasUpstream = n.HasUpstream.Value;
        if (n.DefaultBranchBehind.HasValue) wr.DefaultBranchBehindCommits = n.DefaultBranchBehind;
        if (n.DefaultBranchAhead.HasValue) wr.DefaultBranchAheadCommits = n.DefaultBranchAhead;
        var hasValidVersion = n.Version != "-" && n.Branch != "-";
        var hasDefaultBranch = !string.IsNullOrWhiteSpace(wr.DefaultBranchName);
        // When there is an error message (e.g. fetch failed), keep status InSync so the UI does not show "retry"; the error is shown in the error badge only.
        if (!string.IsNullOrWhiteSpace(n.ErrorMessage))
        {
            wr.SyncStatus = RepoSyncStatus.InSync;
        }
        else
        {
            wr.SyncStatus = !hasValidVersion
                ? RepoSyncStatus.Error
                : (hasDefaultBranch ? RepoSyncStatus.InSync : RepoSyncStatus.NeedsSync);
        }

        await dbContext.SaveChangesAsync();

        if (n.Projects is { Count: > 0 })
        {
            var syncProjects = n.Projects
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new SyncProjectInfo(
                    p.Name.Trim(),
                    p.ProjectType >= 0 && p.ProjectType <= 4 ? (ProjectType)p.ProjectType : ProjectType.Library,
                    p.ProjectPath ?? "",
                    p.TargetFramework ?? "",
                    p.PackageId,
                    (p.PackageReferences ?? new List<RepositorySyncPackageReferenceNotification>())
                        .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                        .Select(pr => new SyncPackageReference(pr.Name.Trim(), pr.Version ?? ""))
                        .ToList()))
                .ToList();

            await workspaceProjectRepository.MergeWorkspaceProjectsAsync(n.WorkspaceId, n.RepositoryId, syncProjects);
            await workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(
                n.WorkspaceId,
                [(n.RepositoryId, (IReadOnlyList<SyncProjectInfo>?)syncProjects)],
                persistDependencyLevel: false);
        }

        var allLinks = await dbContext.WorkspaceRepositories
            .Where(w => w.WorkspaceId == n.WorkspaceId)
            .Select(w => w.SyncStatus)
            .ToListAsync();
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);

        var workspace = await dbContext.Workspaces.FindAsync(n.WorkspaceId);
        if (workspace != null)
        {
            workspace.LastSyncedAt = DateTime.UtcNow;
            workspace.IsInSync = isInSync;
            await dbContext.SaveChangesAsync();
        }

        await workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(n.WorkspaceId);

        var workspacePullRequestService = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();
        await workspacePullRequestService.RefreshPullRequestsAsync(n.WorkspaceId, [n.RepositoryId]);

        await hubContext.Clients.All.SendAsync("WorkspaceSynced", n.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(n.ErrorMessage))
            await hubContext.Clients.All.SendAsync("RepositoryError", n.WorkspaceId, n.RepositoryId, n.ErrorMessage);
        logger.LogDebug("SyncCommand persisted: workspace={WorkspaceId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            n.WorkspaceId, n.RepositoryId, n.Version, n.Branch);
    }
}
