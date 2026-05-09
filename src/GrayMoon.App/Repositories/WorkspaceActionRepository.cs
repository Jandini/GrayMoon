using System.Text.Json;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Persistence for CI action status per workspace–repository link. Single place for all action table read/write.</summary>
public sealed class WorkspaceActionRepository(AppDbContext dbContext, ILogger<WorkspaceActionRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Returns persisted action state for all repositories in the workspace, keyed by RepositoryId. Missing row omitted (never checked).</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Action)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, RepositoryActionsPersistedState>();
        foreach (var link in links)
        {
            if (link.Action == null) continue;

            IReadOnlyList<ActionStatusInfo> workflows;
            if (!string.IsNullOrWhiteSpace(link.Action.WorkflowsJson))
            {
                var list = JsonSerializer.Deserialize<List<ActionStatusInfo>>(link.Action.WorkflowsJson, JsonOptions) ?? [];
                workflows = list;
            }
            else
            {
                workflows = [link.Action.ToActionStatusInfo()];
            }

            result[link.RepositoryId] = new RepositoryActionsPersistedState
            {
                BranchName = link.Action.BranchName,
                Workflows = workflows
            };
        }

        return result;
    }

    /// <summary>Inserts or updates the action row for the given workspace–repo link. JSON is the source of truth; legacy scalar columns are cleared.</summary>
    public async Task UpsertAsync(int workspaceRepositoryId, IReadOnlyList<ActionStatusInfo> workflows, string? branchName, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(workflows, JsonOptions);
        var existing = await dbContext.WorkspaceRepositoryActions
            .FirstOrDefaultAsync(a => a.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);

        if (existing != null)
        {
            existing.BranchName = branchName;
            existing.WorkflowsJson = json;
            existing.LastCheckedAt = now;
            existing.Status = null;
            existing.HtmlUrl = null;
            existing.UpdatedAt = null;
            existing.RunId = null;
            existing.WorkflowId = null;
            existing.WorkflowName = null;
        }
        else
        {
            dbContext.WorkspaceRepositoryActions.Add(new WorkspaceRepositoryAction
            {
                WorkspaceRepositoryId = workspaceRepositoryId,
                BranchName = branchName,
                WorkflowsJson = json,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Upserted Actions for WorkspaceRepositoryId={WorkspaceRepositoryId}, Branch={Branch}, WorkflowCount={Count}",
            workspaceRepositoryId, branchName, workflows.Count);
    }
}
