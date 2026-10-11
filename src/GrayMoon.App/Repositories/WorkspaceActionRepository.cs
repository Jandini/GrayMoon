using System.Text.Json;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

/// <summary>Persistence for CI action status per workspace-repository link. Single place for all action table read/write.</summary>
public sealed class WorkspaceActionRepository(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<WorkspaceActionRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Returns persisted action state for all repositories in the workspace, keyed by RepositoryId. Missing row omitted (never checked).</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Action)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, RepositoryActionsPersistedState>();
        foreach (var link in links)
        {
            if (link.Action == null) continue;

            result[link.RepositoryId] = new RepositoryActionsPersistedState
            {
                BranchName = link.Action.BranchName,
                Workflows = ReadWorkflows(link.Action.WorkflowsJson, link.Action.ToActionStatusInfo())
            };
        }

        return result;
    }

    /// <summary>
    /// Inserts or updates the action row for the given workspace-repo link. JSON is the source of truth; legacy scalar columns are cleared.
    /// Uses its own factory context so grid poll and push CI refresh can upsert different repositories on one circuit.
    /// </summary>
    public async Task UpsertAsync(int workspaceRepositoryId, IReadOnlyList<ActionStatusInfo> workflows, string? branchName, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await UpsertCoreAsync(dbContext, workspaceRepositoryId, workflows, branchName, cancellationToken);
        logger.LogTrace("Upserted Actions for WorkspaceRepositoryId={WorkspaceRepositoryId}, Branch={Branch}, WorkflowCount={Count}",
            workspaceRepositoryId, branchName, workflows.Count);
    }

    /// <summary>Context-aware counterpart to <see cref="GetByWorkspaceIdAsync"/> - reads <see cref="WorkspaceRepositoryContextAction"/> for the given Feature context instead of the special-Workspace-only link row. See design doc §18.</summary>
    public async Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetByWorkspaceIdContextAsync(
        int workspaceId, int contextId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await dbContext.WorkspaceRepositoryContextActions
            .AsNoTracking()
            .Where(a => a.WorkspaceFeatureContextId == contextId && a.WorkspaceRepository!.WorkspaceId == workspaceId)
            .Include(a => a.WorkspaceRepository)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, RepositoryActionsPersistedState>();
        foreach (var row in rows)
        {
            if (row.WorkspaceRepository == null) continue;

            result[row.WorkspaceRepository.RepositoryId] = new RepositoryActionsPersistedState
            {
                BranchName = row.BranchName,
                Workflows = ReadWorkflows(row.WorkflowsJson, row.ToActionStatusInfo())
            };
        }

        return result;
    }

    /// <summary>Context-aware counterpart to <see cref="UpsertAsync"/> - writes <see cref="WorkspaceRepositoryContextAction"/> for the given Feature context instead of the special-Workspace-only link row.</summary>
    public async Task UpsertContextAsync(
        int contextId, int workspaceRepositoryId, IReadOnlyList<ActionStatusInfo> workflows, string? branchName, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(workflows, JsonOptions);
        var existing = await dbContext.WorkspaceRepositoryContextActions
            .FirstOrDefaultAsync(
                a => a.WorkspaceFeatureContextId == contextId && a.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);

        if (existing != null)
        {
            ClearScalars(existing);
            existing.BranchName = branchName;
            existing.WorkflowsJson = json;
            existing.LastCheckedAt = now;
        }
        else
        {
            dbContext.WorkspaceRepositoryContextActions.Add(new WorkspaceRepositoryContextAction
            {
                WorkspaceFeatureContextId = contextId,
                WorkspaceRepositoryId = workspaceRepositoryId,
                BranchName = branchName,
                WorkflowsJson = json,
                LastCheckedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace(
            "Upserted context Actions for ContextId={ContextId}, WorkspaceRepositoryId={WorkspaceRepositoryId}, Branch={Branch}, WorkflowCount={Count}",
            contextId, workspaceRepositoryId, branchName, workflows.Count);
    }

    private static async Task UpsertCoreAsync(
        AppDbContext dbContext,
        int workspaceRepositoryId,
        IReadOnlyList<ActionStatusInfo> workflows,
        string? branchName,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(workflows, JsonOptions);
        var existing = await dbContext.WorkspaceRepositoryActions
            .FirstOrDefaultAsync(a => a.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);

        if (existing != null)
        {
            ClearScalars(existing);
            existing.BranchName = branchName;
            existing.WorkflowsJson = json;
            existing.LastCheckedAt = now;
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
    }

    private static void ClearScalars(WorkspaceRepositoryAction existing)
    {
        existing.Status = null;
        existing.HtmlUrl = null;
        existing.UpdatedAt = null;
        existing.RunId = null;
        existing.WorkflowId = null;
        existing.WorkflowName = null;
    }

    private static void ClearScalars(WorkspaceRepositoryContextAction existing)
    {
        existing.Status = null;
        existing.HtmlUrl = null;
        existing.UpdatedAt = null;
        existing.RunId = null;
        existing.WorkflowId = null;
        existing.WorkflowName = null;
    }

    private static IReadOnlyList<ActionStatusInfo> ReadWorkflows(string? workflowsJson, ActionStatusInfo legacy)
    {
        if (!string.IsNullOrWhiteSpace(workflowsJson))
            return JsonSerializer.Deserialize<List<ActionStatusInfo>>(workflowsJson, JsonOptions) ?? [];
        return [legacy];
    }
}
