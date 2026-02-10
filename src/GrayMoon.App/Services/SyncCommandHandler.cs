using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>Handles SyncCommand from the agent (hook flow): persist version/branch/commit counts and broadcast WorkspaceSynced.</summary>
public sealed class SyncCommandHandler(
    IServiceScopeFactory scopeFactory,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<SyncCommandHandler> logger)
{
    public async Task HandleAsync(int workspaceId, int repositoryId, string version, string branch, int? outgoingCommits = null, int? incomingCommits = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (wr == null)
        {
            logger.LogWarning("SyncCommand: workspace {WorkspaceId} repo {RepositoryId} not found", workspaceId, repositoryId);
            return;
        }

        wr.GitVersion = version == "-" ? null : version;
        wr.BranchName = branch == "-" ? null : branch;
        if (outgoingCommits.HasValue) wr.OutgoingCommits = outgoingCommits;
        if (incomingCommits.HasValue) wr.IncomingCommits = incomingCommits;
        wr.SyncStatus = (version == "-" || branch == "-") ? RepoSyncStatus.Error : RepoSyncStatus.InSync;

        await dbContext.SaveChangesAsync();

        var allLinks = await dbContext.WorkspaceRepositories
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => w.SyncStatus)
            .ToListAsync();
        var isInSync = allLinks.Count > 0 && allLinks.All(s => s == RepoSyncStatus.InSync);

        var workspace = await dbContext.Workspaces.FindAsync(workspaceId);
        if (workspace != null)
        {
            workspace.LastSyncedAt = DateTime.UtcNow;
            workspace.IsInSync = isInSync;
            await dbContext.SaveChangesAsync();
        }

        await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        logger.LogDebug("SyncCommand persisted: workspace={WorkspaceId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            workspaceId, repositoryId, version, branch);
    }
}
