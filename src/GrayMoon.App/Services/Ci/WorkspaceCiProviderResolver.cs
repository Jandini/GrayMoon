using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Ci;

/// <summary>Selects the <see cref="IWorkspaceCiProvider"/> for a workspace. The only place that maps <see cref="WorkspaceCiProvider"/> to behaviour.</summary>
public interface IWorkspaceCiProviderResolver
{
    /// <summary>Resolves the workspace's capabilities and returns its provider. Throws when the workspace does not exist.</summary>
    Task<IWorkspaceCiProvider> GetForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Returns the provider for an already-known <see cref="WorkspaceCapabilities.CiProvider"/> (no database read).</summary>
    IWorkspaceCiProvider Get(WorkspaceCiProvider ciProvider);
}

/// <summary>
/// Resolves by workspace id only, like <see cref="IWorkspaceCapabilitiesResolver"/>: Features inherit the
/// Workspace's CI provider, so there is no context parameter.
/// </summary>
public sealed class WorkspaceCiProviderResolver(
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    GitHubActionsCiProvider gitHubActionsProvider) : IWorkspaceCiProviderResolver
{
    public async Task<IWorkspaceCiProvider> GetForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);
        return Get(capabilities.CiProvider);
    }

    /// <remarks>An unknown value (a newer database read by an older build) selects no CI, the side that does no work.</remarks>
    public IWorkspaceCiProvider Get(WorkspaceCiProvider ciProvider) => ciProvider switch
    {
        WorkspaceCiProvider.GitHubActions => gitHubActionsProvider,
        _ => NoCiProvider.Instance,
    };
}
