using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Versions;

/// <summary>
/// The start check of Update, Update and Push, Prepare Workspace and bulk Push (design D3, I8): resolves the effective
/// versions of the repositories in scope with fresh registry answers. When any of them is unresolved the operation
/// stops with <see cref="EffectiveVersionResolution.StopMessage"/> before it changes anything. A push of one named
/// repository does not run it.
/// </summary>
public sealed class EffectiveVersionStartCheck(EffectiveVersionResolver resolver, ILogger<EffectiveVersionStartCheck> logger)
{
    /// <summary>Resolves <paramref name="repositoryIds"/> (every repository when null). Returns the resolution; a non-null <see cref="EffectiveVersionResolution.StopMessage"/> means stop.</summary>
    public async Task<EffectiveVersionResolution> RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int>? repositoryIds,
        string operationName,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(workspaceId, contextId, repositoryIds, bypassCache: true, cancellationToken);
        var unresolved = resolution.Unresolved;
        if (unresolved.Count > 0)
        {
            logger.LogWarning(
                "{Operation} stopped before any change in workspace {WorkspaceId} context {ContextId}: {Count} repositories have unverifiable packages ({Repositories})",
                operationName, workspaceId, contextId.Value, unresolved.Count,
                string.Join(", ", unresolved.Select(d => $"{d.RepositoryName}: {d.ReasonCode}")));
        }

        return resolution;
    }
}
