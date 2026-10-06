using GrayMoon.App.Data;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Loads the <see cref="HostPrerequisiteRequirements"/> for the workspaces that currently exist. Uses a
/// short-lived context per call because it runs from circuit-scoped monitors and pages concurrently with
/// other work on the same circuit (AGENTS.md "DbContext handling").
/// </summary>
public sealed class HostPrerequisiteRequirementsProvider(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    ILogger<HostPrerequisiteRequirementsProvider> logger)
{
    /// <summary>
    /// Falls back to <see cref="HostPrerequisiteRequirements.All"/> when the workspaces cannot be read, so a
    /// genuinely missing tool is never hidden by a database error.
    /// </summary>
    public async Task<HostPrerequisiteRequirements> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<int> workspaceIds;
            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                workspaceIds = await db.Workspaces
                    .AsNoTracking()
                    .Select(workspace => workspace.WorkspaceId)
                    .ToListAsync(cancellationToken);
            }

            var capabilities = await capabilitiesResolver.GetManyAsync(workspaceIds, cancellationToken);
            return HostPrerequisiteRequirements.For(capabilities.Values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Host prerequisite requirements; treating every prerequisite as required");
            return HostPrerequisiteRequirements.All;
        }
    }
}
