using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>Handles SyncCommand from the agent (hook flow): persist version/branch and broadcast WorkspaceSynced.</summary>
public sealed class SyncCommandHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<WorkspaceSyncHub> _hubContext;
    private readonly ILogger<SyncCommandHandler> _logger;

    public SyncCommandHandler(
        IServiceScopeFactory scopeFactory,
        IHubContext<WorkspaceSyncHub> hubContext,
        ILogger<SyncCommandHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task HandleAsync(int workspaceId, int repositoryId, string version, string branch)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.GitHubRepositoryId == repositoryId);
        if (wr == null)
        {
            _logger.LogWarning("SyncCommand: workspace {WorkspaceId} repo {RepositoryId} not found", workspaceId, repositoryId);
            return;
        }

        wr.GitVersion = version == "-" ? null : version;
        wr.BranchName = branch == "-" ? null : branch;
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

        await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
        _logger.LogDebug("SyncCommand persisted: workspace={WorkspaceId}, repo={RepositoryId}, version={Version}, branch={Branch}",
            workspaceId, repositoryId, version, branch);
    }
}
