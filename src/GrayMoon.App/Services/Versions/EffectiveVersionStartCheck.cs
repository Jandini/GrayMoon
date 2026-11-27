using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Versions;

/// <summary>
/// The start check of Update, Update and Push, Prepare Workspace and bulk Push (design D3, I8): resolves the effective
/// versions of the repositories in scope with fresh registry answers. When any of them is unresolved the operation
/// stops with <see cref="EffectiveVersionResolution.StopMessage"/> before it changes anything. A push of one named
/// repository does not run it.
/// </summary>
public sealed class EffectiveVersionStartCheck(
    EffectiveVersionResolver resolver,
    ILogger<EffectiveVersionStartCheck> logger,
    GrayMoon.App.Services.Features.FeatureFinalizationCoordinator? finalizationCoordinator = null)
{
    /// <summary>The overlay text while an operation waits for the version stage of a Feature that was just created.</summary>
    internal const string WaitingForVersionsMessage = "Verifying published packages...";

    /// <summary>Resolves <paramref name="repositoryIds"/> (every repository when null). Returns the resolution; a non-null <see cref="EffectiveVersionResolution.StopMessage"/> means stop.</summary>
    public async Task<EffectiveVersionResolution> RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int>? repositoryIds,
        string operationName,
        CancellationToken cancellationToken,
        IProgress<GrayMoon.Application.OperationProgress>? progress = null)
    {
        // A Feature that was just created is still deciding its versions in the background (FeatureVersionStage). The
        // operation waits for that instead of calculating the same versions beside it; the resolution below then reuses
        // the parent versions and asks only the registry again. A wait that runs out is not a reason to stop: the
        // resolution below is authoritative by itself.
        if (finalizationCoordinator?.IsFinalizing(contextId.Value) == true)
        {
            progress?.Report(new GrayMoon.Application.OperationProgress(WaitingForVersionsMessage));
            try
            {
                await finalizationCoordinator.WaitForFinalizationAsync(contextId.Value, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "{Operation} did not wait any longer for the version stage of context {ContextId}; resolving now.", operationName, contextId.Value);
            }
        }

        var resolution = await resolver.ResolveAsync(workspaceId, contextId, repositoryIds, bypassCache: true, cancellationToken, progress);
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
