using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Applies <see cref="WorkspacePageAccess"/> to a persisted workspace. Resolves by workspace id only (design
/// section 3), so a Feature-context route (<c>?context=</c>) gets exactly its parent Workspace's answer.
/// </summary>
public interface IWorkspacePageAccessResolver
{
    Task<WorkspacePageAccessOutcome> CheckAsync(int workspaceId, WorkspacePage page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks access and runs <paramref name="loadAsync"/> only when the page is available, so an unavailable
    /// page never reaches its data queries.
    /// </summary>
    Task<WorkspacePageAccessOutcome> LoadIfAvailableAsync(
        int workspaceId,
        WorkspacePage page,
        Func<Task> loadAsync,
        CancellationToken cancellationToken = default);

    /// <summary>The navigation item set for a workspace; a missing workspace yields only the always-available pages.</summary>
    Task<IReadOnlyList<WorkspacePage>> GetAvailablePagesAsync(int workspaceId, CancellationToken cancellationToken = default);
}

/// <remarks>
/// Uses <see cref="IWorkspaceCapabilitiesResolver.GetManyAsync"/> rather than <c>GetAsync</c> so a missing or
/// deleted workspace is an outcome, not an exception. The capability resolver creates its own short-lived
/// DbContext per call, which is what makes this safe from the static-SSR <c>NavMenu</c>.
/// </remarks>
public sealed class WorkspacePageAccessResolver(IWorkspaceCapabilitiesResolver capabilitiesResolver)
    : IWorkspacePageAccessResolver
{
    public async Task<WorkspacePageAccessOutcome> CheckAsync(
        int workspaceId,
        WorkspacePage page,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await TryGetCapabilitiesAsync(workspaceId, cancellationToken);
        return WorkspacePageAccess.Evaluate(page, capabilities);
    }

    public async Task<WorkspacePageAccessOutcome> LoadIfAvailableAsync(
        int workspaceId,
        WorkspacePage page,
        Func<Task> loadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadAsync);

        var outcome = await CheckAsync(workspaceId, page, cancellationToken);
        if (outcome == WorkspacePageAccessOutcome.Available)
            await loadAsync();

        return outcome;
    }

    public async Task<IReadOnlyList<WorkspacePage>> GetAvailablePagesAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await TryGetCapabilitiesAsync(workspaceId, cancellationToken);
        return WorkspacePageAccess.GetAvailablePages(capabilities);
    }

    private async Task<WorkspaceCapabilities?> TryGetCapabilitiesAsync(int workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId <= 0)
            return null;

        var found = await capabilitiesResolver.GetManyAsync([workspaceId], cancellationToken);
        return found.TryGetValue(workspaceId, out var capabilities) ? capabilities : null;
    }
}
