using System.Threading.Channels;
using GrayMoon.Application;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// The update lane of <see cref="UpdateAndPushOrchestrator"/>. Implemented by <see cref="DependencyUpdateOrchestrator"/>;
/// the interface exists so the pipeline wiring (hand-off order, failure settling) can be tested with fake lanes.
/// </summary>
public interface IDependencyUpdateLane
{
    Task<DependencyUpdateRunResult> RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string> onRepoError,
        Action<int, string> onLevelError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null,
        Func<DependencyLevelCompletion, Task>? onLevelCompleted = null);
}

/// <summary>
/// The push lane of <see cref="UpdateAndPushOrchestrator"/>. Implemented by <see cref="WorkspacePushService"/>; see
/// <see cref="IDependencyUpdateLane"/> for why it is an interface.
/// </summary>
public interface IPushLane
{
    Task<bool> CanRunPushLaneAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken);

    Task<int> RunPushLaneAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        ChannelReader<DependencyLevelCompletion> completedLevels,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action<int, string>? onLevelError = null,
        bool restorePackages = true,
        string? runId = null,
        CancellationToken cancellationToken = default);
}
