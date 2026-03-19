using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Persistence for CI action status per workspace–repository link. Single place for all action table read/write.</summary>
public sealed class WorkspaceActionRepository(AppDbContext dbContext, ILogger<WorkspaceActionRepository> logger)
{
    /// <summary>Returns persisted action state for all repositories in the workspace, keyed by RepositoryId. Missing row yields null (never checked).</summary>
    public async Task<IReadOnlyDictionary<int, ActionStatusInfo?>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Action)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, ActionStatusInfo?>();
        foreach (var link in links)
        {
            if (link.Action == null) continue;
            result[link.RepositoryId] = link.Action.ToActionStatusInfo();
        }
        return result;
    }

    /// <summary>Inserts or updates the action row for the given workspace–repo link.</summary>
    public async Task UpsertAsync(int workspaceRepositoryId, ActionStatusInfo? info, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existing = await dbContext.WorkspaceRepositoryActions
            .FirstOrDefaultAsync(a => a.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);

        if (existing != null)
        {
            existing.Status = info?.Status;
            existing.HtmlUrl = info?.HtmlUrl;
            existing.UpdatedAt = info?.UpdatedAt;
            existing.BranchName = info?.BranchName;
            existing.RunId = info?.RunId;
            existing.WorkflowId = info?.WorkflowId;
            existing.WorkflowName = info?.WorkflowName;
            existing.LastCheckedAt = now;
        }
        else
        {
            dbContext.WorkspaceRepositoryActions.Add(new WorkspaceRepositoryAction
            {
                WorkspaceRepositoryId = workspaceRepositoryId,
                Status = info?.Status,
                HtmlUrl = info?.HtmlUrl,
                UpdatedAt = info?.UpdatedAt,
                BranchName = info?.BranchName,
                RunId = info?.RunId,
                WorkflowId = info?.WorkflowId,
                WorkflowName = info?.WorkflowName,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Upserted Action for WorkspaceRepositoryId={WorkspaceRepositoryId}, Status={Status}, Branch={Branch}",
            workspaceRepositoryId, info?.Status, info?.BranchName);
    }
}
