using GrayMoon.Application.Repositories.Policy;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// Application boundary for push. Resolves the workspace's capabilities once per call and selects the push
/// strategy from them; everything below receives the chosen strategy instead of checking the workspace type.
/// </summary>
public sealed class WorkspacePushOperations(
    WorkspacePushHandler pushHandler,
    WorkspaceRepository workspaceRepository,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    WorkspacePushStrategySelector strategySelector,
    WorkspaceOperationOutcomeStore outcomes,
    IWorkspaceRepositoryOperationPolicy operationPolicy,
    GrayMoon.App.Services.Policy.ArchivedRepositoryVerifier archivedVerifier,
    ILogger<WorkspacePushOperations> logger,
    GrayMoon.App.Services.Versions.EffectiveVersionStartCheck effectiveVersionStartCheck) : IWorkspacePushOperations
{
    /// <summary>The overlay text while the plan is built and before the first repository is verified.</summary>
    internal const string PreparingPushMessage = "Preparing push...";

    /// <summary>The overlay text for the counted part of the plan: "Preparing push..." until a repository is verified.</summary>
    internal static string PreparedMessage(int? completed, int? total) =>
        completed is > 0 && total is > 0
            ? $"Push prepared for {completed} of {total} repositories..."
            : PreparingPushMessage;

    /// <summary>Shows the start check's counts in the push's own words (<see cref="PreparedMessage"/>).</summary>
    private sealed class PreparedCountProgress(IProgress<OperationProgress> inner) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) =>
            inner.Report(new OperationProgress(PreparedMessage(value.Completed, value.Total), value.Completed, value.Total));
    }

    public async Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        // A repository marked archived is left out of the plan, so ask the provider first whether it still is.
        progress.Report(PreparingPushMessage);
        await archivedVerifier.VerifyAsync(workspaceId, cancellationToken);

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        return await GetPlanForLinksAsync(workspaceId, contextId, workspace.Repositories.ToList(), maxLevel, cancellationToken, progress);
    }

    public async Task<IReadOnlySet<int>> GetRepositoryIdsNeedingPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        if (repositoryIds.Count == 0)
            return new HashSet<int>();

        return await workspaceRepository.GetRepositoryIdsNeedingPushAsync(workspaceId, contextId.Value, repositoryIds, cancellationToken);
    }

    public async Task<WorkspacePushPlan> GetPlanForLinksAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        int? maxLevel = null,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        // Start check (effective versions, D3): the selection below reads the effective version kinds, so they are resolved
        // with fresh registry answers first. An unverifiable package stops the push before anything is pushed.
        var resolution = await effectiveVersionStartCheck.RunAsync(
            workspaceId, contextId, repositoryIds: null, "Push", cancellationToken,
            progress is null ? null : new PreparedCountProgress(progress));
        if (resolution.StopMessage is { } stopMessage)
        {
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false)
            {
                StopMessage = stopMessage,
            };
        }

        var noWork =await workspaceRepository.GetPushNoWorkReasonsAsync(workspaceId, contextId.Value, links.Select(l => l.RepositoryId).ToHashSet(), cancellationToken);
        foreach (var line in noWork)
            logger.LogInformation("Push plan for workspace {WorkspaceId} context {ContextId}: {NoWork}", workspaceId, contextId.Value, line);

        var (_, strategy) = await SelectStrategyAsync(workspaceId, cancellationToken);
        var (_, pushRepoIds, hasUnpushed) = await pushHandler.GetPushPlanAsync(strategy, workspaceId, contextId, links, cancellationToken, maxLevel);
        if (!hasUnpushed || pushRepoIds.Count == 0)
            return new WorkspacePushPlan(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false) { NoWork = noWork };

        var required = await strategy.GetRequiredPackageIdsAsync(workspaceId, contextId, pushRepoIds, cancellationToken);
        return new WorkspacePushPlan(pushRepoIds, required, true) { NoWork = noWork };
    }

    public async Task<OperationResult> PushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IProgress<OperationProgress>? progress = null,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true)
    {
        await archivedVerifier.VerifyAsync(workspaceId, cancellationToken);

        var (capabilities, strategy) = await SelectStrategyAsync(workspaceId, cancellationToken);
        var run = new WorkspacePushRun(
            workspaceId,
            contextId,
            capabilities,
            repositoryIds,
            synchronizedPush,
            requiredPackageIds,
            syncedRepoIds,
            restorePackages,
            runId);
        var recorder = outcomes.Begin(workspaceId, contextId, "Push", runId);
        var result = await pushHandler.RunPushWithDependenciesAsync(strategy, run, progress, cancellationToken: cancellationToken);
        if (!result.Success)
            recorder.Result(result);

        var skipped = (await operationPolicy.PartitionAsync(
            workspaceId, contextId, RepositoryOperation.Push, repositoryIds.ToList(), RepositorySubjectSource.Context, cancellationToken)).Skipped;
        if (skipped.Count == 0)
            return result;

        logger.LogInformation(
            "Push {RunId}: workspace {WorkspaceId}: {Count} repositories left out of the push: {Repositories}",
            runId, workspaceId, skipped.Count, string.Join(", ", skipped.Select(skip => $"{skip.RepositoryName} ({skip.Reason})")));
        return result with { Skipped = skipped };
    }

    public async Task<OperationResult> PushPendingAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool synchronizedPush,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool restorePackages = true)
    {
        var plan = await GetPlanAsync(workspaceId, contextId, maxLevel: null, cancellationToken, progress);
        if (plan.StopMessage is { } stopMessage)
            return OperationResult.Fail(stopMessage);
        if (!plan.HasUnpushed)
            return OperationResult.Ok();

        return await PushAsync(
            workspaceId,
            contextId,
            plan.RepositoryIds,
            synchronizedPush,
            plan.RequiredPackageIds,
            progress,
            cancellationToken: cancellationToken,
            restorePackages: restorePackages);
    }

    public Task<OperationResult> PushSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => pushHandler.PushSingleRepositoryWithUpstreamAsync(
            workspaceId,
            contextId,
            repositoryId,
            branchName,
            progress,
            cancellationToken);

    private async Task<(WorkspaceCapabilities Capabilities, IWorkspacePushStrategy Strategy)> SelectStrategyAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);
        return (capabilities, strategySelector.Select(capabilities));
    }
}
