using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// How a workspace's repositories are planned and pushed. Picked once per operation by
/// <see cref="WorkspacePushStrategySelector"/> from the workspace's capabilities, so nothing below the
/// application boundary has to ask what kind of workspace it is pushing.
/// </summary>
public interface IWorkspacePushStrategy
{
    /// <summary>Every pushable repository scoped to <paramref name="contextId"/>, in the order this strategy pushes them.</summary>
    Task<IReadOnlyList<PushRepoPayload>> GetPayloadAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken);

    /// <summary>Package ids the <paramref name="repositoryIds"/> must find in a registry before they can be pushed. Empty when the strategy does not wait for packages.</summary>
    Task<IReadOnlySet<string>> GetRequiredPackageIdsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken);

    /// <summary>Pushes <see cref="WorkspacePushRun.RepositoryIds"/>, reporting failures through the callbacks instead of throwing.</summary>
    Task PushAsync(
        WorkspacePushRun run,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action<int, string> onLevelError,
        Action? onAppSideComplete,
        CancellationToken cancellationToken);
}

/// <summary>One push request as the strategies see it.</summary>
public sealed record WorkspacePushRun(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    WorkspaceCapabilities Capabilities,
    IReadOnlySet<int> RepositoryIds,
    bool SynchronizedPush,
    IReadOnlySet<string> RequiredPackageIds,
    IReadOnlySet<int>? SyncedRepoIds,
    bool RestorePackages,
    string? RunId);

/// <summary>Maps workspace capabilities to the push strategy that serves them.</summary>
public sealed class WorkspacePushStrategySelector(
    BasicGitPushStrategy basicGitPushStrategy,
    DotNetDependencyPushStrategy dotNetDependencyPushStrategy)
{
    public IWorkspacePushStrategy Select(WorkspaceCapabilities capabilities)
        => capabilities.UsesDependencyAwarePush ? dotNetDependencyPushStrategy : basicGitPushStrategy;
}
