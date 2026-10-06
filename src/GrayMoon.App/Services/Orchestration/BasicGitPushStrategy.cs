using GrayMoon.App.Models;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Plain multi-repository git push: every selected repository in parallel. Reads no project or package
/// dependency data, waits for no registry, orders nothing by dependency level and restores nothing.
/// </summary>
public sealed class BasicGitPushStrategy(WorkspacePushService workspacePushService) : IWorkspacePushStrategy
{
    private static readonly IReadOnlySet<string> NoPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<PushRepoPayload>> GetPayloadAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
        => workspacePushService.GetPushPayloadWithoutDependenciesAsync(workspaceId, contextId.Value, cancellationToken);

    public Task<IReadOnlySet<string>> GetRequiredPackageIdsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken)
        => Task.FromResult(NoPackages);

    public async Task PushAsync(
        WorkspacePushRun run,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action<int, string> onLevelError,
        Action? onAppSideComplete,
        CancellationToken cancellationToken)
    {
        setProgress("Pushing...");
        var payload = await GetPayloadAsync(run.WorkspaceId, run.ContextId, cancellationToken);
        await workspacePushService.RunPushReposParallelAsync(
            run.WorkspaceId,
            run.ContextId,
            payload,
            run.RepositoryIds,
            setProgress,
            onRepoError,
            onAppSideComplete: null,
            cancellationToken: cancellationToken);
    }
}
