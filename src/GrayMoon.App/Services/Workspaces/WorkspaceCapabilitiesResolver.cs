using GrayMoon.App.Data;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Reads the three persisted profile settings off a Workspace row and turns them into a
/// <see cref="WorkspaceCapabilities"/> record.
/// </summary>
/// <remarks>
/// Creates a short-lived context per call from <see cref="IDbContextFactory{TContext}"/> rather than taking an
/// injected scoped <see cref="AppDbContext"/>: this is called from Blazor pages (where one circuit is one shared
/// context) and from concurrent fan-outs, and a shared context would be a real concurrency hazard - see
/// AGENTS.md "DbContext handling".
/// </remarks>
public sealed class WorkspaceCapabilitiesResolver(IDbContextFactory<AppDbContext> dbContextFactory)
    : IWorkspaceCapabilitiesResolver
{
    public async Task<WorkspaceCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var profile = await db.Workspaces
            .AsNoTracking()
            .Where(workspace => workspace.WorkspaceId == workspaceId)
            .Select(workspace => new { workspace.Type, workspace.VersioningMode, workspace.CiProvider })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            throw new InvalidOperationException(
                $"Cannot resolve workspace capabilities: Workspace {workspaceId} does not exist.");
        }

        return new WorkspaceCapabilities(profile.Type, profile.VersioningMode, profile.CiProvider);
    }

    /// <summary>
    /// Resolves several workspaces in one query. Ids that do not exist are silently omitted rather than
    /// throwing: callers are list surfaces resolving a set of rows, and a workspace deleted between building
    /// that set and resolving it must not fail the whole page. Callers that need a specific workspace to exist
    /// use <see cref="GetAsync"/>, which does throw.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, WorkspaceCapabilities>> GetManyAsync(
        IReadOnlyCollection<int> workspaceIds,
        CancellationToken cancellationToken = default)
    {
        if (workspaceIds.Count == 0)
            return new Dictionary<int, WorkspaceCapabilities>();

        var distinctIds = workspaceIds.Distinct().ToList();

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var profiles = await db.Workspaces
            .AsNoTracking()
            .Where(workspace => distinctIds.Contains(workspace.WorkspaceId))
            .Select(workspace => new
            {
                workspace.WorkspaceId,
                workspace.Type,
                workspace.VersioningMode,
                workspace.CiProvider
            })
            .ToListAsync(cancellationToken);

        return profiles.ToDictionary(
            profile => profile.WorkspaceId,
            profile => new WorkspaceCapabilities(profile.Type, profile.VersioningMode, profile.CiProvider));
    }
}
