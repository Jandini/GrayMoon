using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceFeatureContextResolver(IDbContextFactory<AppDbContext> dbContextFactory) : IWorkspaceFeatureContextResolver
{
    public async Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.WorkspaceId == workspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace,
                cancellationToken);
        if (existing is not null)
            return new WorkspaceFeatureContextId(existing.WorkspaceFeatureContextId);

        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found.");

        var now = DateTime.UtcNow;
        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Workspace,
            CreatedAt = now,
            LastSyncedAt = workspace.LastSyncedAt,
            IsInSync = workspace.IsInSync
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync(cancellationToken);

        if (!await db.WorkspaceSelectedFeatureContexts.AnyAsync(s => s.WorkspaceId == workspaceId, cancellationToken))
        {
            db.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext
            {
                WorkspaceId = workspaceId,
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    public async Task<WorkspaceFeatureContextInfo> GetRequiredAsync(
        WorkspaceFeatureContextId contextId,
        int? expectedWorkspaceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Include(c => c.WorkspaceFeature)
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken)
            ?? throw new InvalidOperationException($"WorkspaceFeatureContext {contextId.Value} was not found.");

        if (expectedWorkspaceId is int wsId && row.WorkspaceId != wsId)
            throw new InvalidOperationException($"WorkspaceFeatureContext {contextId.Value} does not belong to workspace {wsId}.");

        return ToInfo(row);
    }

    public async Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Include(c => c.WorkspaceFeature)
            .Where(c => c.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(c => c.Kind == WorkspaceFeatureContextKind.Workspace ? 0 : 1)
            .ThenBy(c => c.WorkspaceFeature?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(ToInfo)
            .ToList();
    }

    private static WorkspaceFeatureContextInfo ToInfo(WorkspaceFeatureContext row) => new()
    {
        ContextId = new WorkspaceFeatureContextId(row.WorkspaceFeatureContextId),
        WorkspaceId = row.WorkspaceId,
        IsSpecialWorkspace = row.Kind == WorkspaceFeatureContextKind.Workspace,
        WorkspaceFeatureId = row.WorkspaceFeatureId,
        FeatureName = row.WorkspaceFeature?.Name,
        LifecycleState = row.WorkspaceFeature?.LifecycleState.ToString(),
        LastSyncedAt = row.LastSyncedAt,
        IsInSync = row.IsInSync
    };
}
