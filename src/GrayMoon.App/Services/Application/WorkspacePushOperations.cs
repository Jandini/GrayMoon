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
    GrayMoon.App.Services.Versions.EffectiveVersionStartCheck effectiveVersionStartCheck,
    GrayMoon.App.Services.Worker.IWorkerSyncNotificationBarrier? syncNotificationBarrier = null,
    AppBackgroundWorkStateService? appBackgroundWork = null) : IWorkspacePushOperations
{
    /// <summary>
    /// The plan is built from stored state: the commit, the outgoing count and the effective version of every
    /// repository. A Git event that is still waiting to be applied (a commit made a moment ago) is not in it yet, and a
    /// plan built without it leaves that repository out as "nothing to push". So the plan waits for the events of the
    /// Workspace that have arrived, as Update does, and counts them down in the overlay meanwhile.
    /// </summary>
    /// <returns>True when the wait was announced in the overlay, so the caller puts its own text back.</returns>
    private async Task<bool> WaitForPendingSyncsAsync(int workspaceId, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (syncNotificationBarrier is null)
            return false;

        // With nothing waiting there is nothing to announce: the wait returns at once and the overlay keeps its text.
        if (appBackgroundWork is null || appBackgroundWork.GetPendingCountForWorkspace(workspaceId) == 0)
        {
            await syncNotificationBarrier.WaitForPendingAsync(workspaceId, cancellationToken);
            return false;
        }

        await GrayMoon.App.Services.Worker.PendingSyncNotificationWait.WaitAsync(
                syncNotificationBarrier, appBackgroundWork, workspaceId, message => progress.Report(message), cancellationToken);
        return true;
    }

    /// <summary>
    /// The overlay text while the plan is built. Nothing is prepared: the plan checks which repositories to push (archived
    /// state, effective versions, selection). The version check then counts its GitVersion runs ("Checked x of y
    /// repositories...", <see cref="Versions.EffectiveVersionResolver.CheckedMessage"/>).
    /// </summary>
    internal const string CheckingVersionsMessage = "Checking versions...";

    public async Task<WorkspacePushPlan> GetPlanAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel = null,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        // Before the links are read: what the pending events store is part of the plan.
        await WaitForPendingSyncsAsync(workspaceId, progress, cancellationToken);

        // A repository marked archived is left out of the plan, so ask the provider first whether it still is.
        progress.Report(CheckingVersionsMessage);
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
        // Nothing is pending any more when GetPlanAsync called this; a caller that brings its own links waits here.
        if (await WaitForPendingSyncsAsync(workspaceId, progress, cancellationToken))
            progress.Report(CheckingVersionsMessage);

        // Start check (effective versions, D3): the selection below reads the effective version kinds, so they are resolved
        // with fresh registry answers first. An unverifiable package stops the push before anything is pushed.
        var resolution = await effectiveVersionStartCheck.RunAsync(
            workspaceId, contextId, repositoryIds: null, "Push", cancellationToken, progress);
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
