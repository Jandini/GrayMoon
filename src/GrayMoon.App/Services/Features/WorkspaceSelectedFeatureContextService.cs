using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceSelectedFeatureContextService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceFeatureContextResolver contextResolver) : IWorkspaceSelectedFeatureContextService
{
    public async Task<WorkspaceFeatureContextId?> GetSelectedAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var selected = await db.WorkspaceSelectedFeatureContexts.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, cancellationToken);
        if (selected is not null)
            return new WorkspaceFeatureContextId(selected.WorkspaceFeatureContextId);

        var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return special;
    }

    public async Task SetSelectedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        _ = await contextResolver.GetRequiredAsync(contextId, expectedWorkspaceId: workspaceId, cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkspaceSelectedFeatureContexts.FindAsync([workspaceId], cancellationToken);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            db.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext
            {
                WorkspaceId = workspaceId,
                WorkspaceFeatureContextId = contextId.Value,
                UpdatedAt = now
            });
        }
        else
        {
            row.WorkspaceFeatureContextId = contextId.Value;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
