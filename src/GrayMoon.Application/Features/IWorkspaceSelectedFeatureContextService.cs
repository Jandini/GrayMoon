namespace GrayMoon.Application.Features;

/// <summary>
/// Navigation preference only - never used as execution authority for mutations/queries.
/// </summary>
public interface IWorkspaceSelectedFeatureContextService
{
    Task<WorkspaceFeatureContextId?> GetSelectedAsync(int workspaceId, CancellationToken cancellationToken = default);

    Task SetSelectedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);
}
