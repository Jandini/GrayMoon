using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// The single entry point for a combined Update and Push (see <see cref="IWorkspaceUpdateAndPushOperations"/>). A thin
/// coordinator over the existing facades: the two-lane pipeline first, then, only when the pipeline changed nothing, the
/// sequential update followed by a synchronized push. It owns the decision policy, the update-success gate, the one
/// correlated outcome and the explicit approval of a normal push; the engine below is unchanged.
/// </summary>
public sealed class WorkspaceUpdateAndPushOperations(
    IWorkspaceUpdateOperations updateOperations,
    IWorkspacePushOperations pushOperations,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceRepositoryLinkListQueryService linkListQuery,
    WorkspaceOperationOutcomeStore outcomes,
    UpdateAndPushApprovalStore approvals,
    ILogger<WorkspaceUpdateAndPushOperations> logger) : IWorkspaceUpdateAndPushOperations
{
    private const string OperationName = "Update and Push";

    public async Task<UpdateAndPushExecutionResult> ExecuteAsync(
        UpdateAndPushCommand command,
        UpdateAndPushObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var runId = command is ApproveNormalPushContinuation
            ? null
            : GrayMoon.Abstractions.Logging.GrayMoonOperationContext.EnsureId();

        var refusal = await ValidateContextAsync(command, runId ?? "none", cancellationToken);
        if (refusal != null)
            return refusal;

        switch (command)
        {
            case StartUpdateAndPush start:
                return await CheckReviewedDefaultBranchesAsync(start, runId!, cancellationToken)
                    ?? await RunAsync(start, runId!, observer, cancellationToken);
            case ApproveNormalPushContinuation approval:
                return await ContinueWithNormalPushAsync(approval, observer, cancellationToken);
            default:
                throw new ArgumentException($"Unsupported command {command.GetType().Name}.", nameof(command));
        }
    }

    public async Task<IReadOnlyList<UpdateAndPushDefaultBranchTarget>> GetDefaultBranchTargetsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken)
        => (await GetDefaultBranchReviewAsync(workspaceId, contextId, maxLevel, cancellationToken)).Targets;

    public async Task<UpdateAndPushDefaultBranchReview> GetDefaultBranchReviewAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken)
    {
        var info = await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        var links = await linkListQuery.GetAllSnapshotsAsync(workspaceId, contextId, info.IsSpecialWorkspace, cancellationToken);
        var onDefault = links.Where(l => IsOnDefaultBranchInScope(l, maxLevel)).ToList();
        if (onDefault.Count == 0)
            return new UpdateAndPushDefaultBranchReview([], new HashSet<int>());

        // The update plan is the more expensive read, so it is only taken when something could be warned about.
        var (updatePlan, _) = await updateOperations.GetUpdatePlanAsync(workspaceId, contextId, null, cancellationToken);
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        return new UpdateAndPushDefaultBranchReview(
            onDefault
                .Where(l => repoIdsWithUpdates.Contains(l.RepositoryId))
                .Select(l => new UpdateAndPushDefaultBranchTarget(l.RepositoryId, l.RepositoryName, l.DefaultBranchName!))
                .ToList(),
            onDefault.Select(l => l.RepositoryId).ToHashSet());
    }

    private static bool IsOnDefaultBranchInScope(WorkspaceRepositoryLinkListItemDto l, int? maxLevel)
        => string.IsNullOrWhiteSpace(l.CheckedOutTag)
            && (maxLevel is null || (l.DependencyLevel ?? 0) <= maxLevel)
            && !string.IsNullOrWhiteSpace(l.DefaultBranchName)
            && string.Equals(l.BranchName, l.DefaultBranchName, StringComparison.Ordinal);

    /// <summary>
    /// One read of the stored branch state (no Worker call, no update plan). Refuses the run when a repository in scope sits
    /// on its default branch now but was not on it when the user was warned.
    /// </summary>
    private async Task<UpdateAndPushExecutionResult?> CheckReviewedDefaultBranchesAsync(
        StartUpdateAndPush start, string runId, CancellationToken cancellationToken)
    {
        if (start.ReviewedDefaultBranchRepoIds is not { } reviewed)
            return null;

        var info = await contextResolver.GetRequiredAsync(start.ContextId, start.WorkspaceId, cancellationToken);
        var links = await linkListQuery.GetAllSnapshotsAsync(start.WorkspaceId, start.ContextId, info.IsSpecialWorkspace, cancellationToken);
        var changed = links
            .Where(l => IsOnDefaultBranchInScope(l, start.MaxLevel) && !reviewed.Contains(l.RepositoryId))
            .Select(l => l.RepositoryName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.Count == 0)
            return null;

        logger.LogWarning("[UpdateAndPush {RunId}] Workspace {WorkspaceId}: refused, {Count} repositories moved onto their default branch after the warning: {Repos}", runId, start.WorkspaceId, changed.Count, string.Join(", ", changed));
        return Rejected(start, runId, $"These repositories moved onto their default branch after you confirmed: {string.Join(", ", changed)}. Review and start Update and Push again.");
    }

    private async Task<UpdateAndPushExecutionResult?> ValidateContextAsync(
        UpdateAndPushCommand command, string runId, CancellationToken cancellationToken)
    {
        if (command.WorkspaceId <= 0)
            return Rejected(command, runId, "The Workspace is not valid.");

        WorkspaceFeatureContextInfo info;
        try
        {
            info = await contextResolver.GetRequiredAsync(command.ContextId, command.WorkspaceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateAndPush {RunId}] Workspace {WorkspaceId}: context {ContextId} is not available.", runId, command.WorkspaceId, command.ContextId.Value);
            return Rejected(command, runId, "The selected Feature is no longer available. Select a Feature or the Workspace and try again.");
        }

        if (!info.IsSpecialWorkspace && string.Equals(info.LifecycleState, "NeedsRepair", StringComparison.OrdinalIgnoreCase))
            return Rejected(command, runId, "The selected Feature needs repair. Repair it before running Update and Push.");

        return null;
    }

    private async Task<UpdateAndPushExecutionResult> RunAsync(
        StartUpdateAndPush start,
        string runId,
        UpdateAndPushObserver? observer,
        CancellationToken cancellationToken)
    {
        var workspaceId = start.WorkspaceId;
        var contextId = start.ContextId;
        var recorder = outcomes.Begin(workspaceId, contextId, OperationName, runId);
        logger.LogInformation(
            "[UpdateAndPush {RunId}] Workspace {WorkspaceId} context {ContextId}: starting. MaxLevel={MaxLevel}, Restore={Restore}, IncludeDeps={IncludeDeps}, Origin={Origin}",
            runId, workspaceId, contextId.Value, start.MaxLevel, start.RestorePackages, start.IncludeDepsInCommitMessage, start.Origin ?? "unspecified");

        Action<int, string> repoError = (id, message) => observer?.SetRepositoryError?.Invoke(id, message);
        Action<int, string> levelError = (level, message) => observer?.SetLevelError?.Invoke(level, message);
        Action<string> report = message => observer?.ReportProgress?.Invoke(message);

        try
        {
            var pipeline = await updateOperations.UpdateAndPushAsync(
                workspaceId, contextId, cancellationToken, report, repoError, levelError,
                start.CommitMessage, start.IncludeDepsInCommitMessage, start.MaxLevel, start.RestorePackages, runId);

            if (pipeline.Pipelined)
                return FromPipeline(start, runId, pipeline);

            // Documented guarantee: Pipelined=false changed nothing. Run the established sequential update and push.
            logger.LogInformation("[UpdateAndPush {RunId}] Workspace {WorkspaceId}: pipeline not possible, running the sequential update then synchronized push.", runId, workspaceId);

            var update = await updateOperations.UpdateAsync(
                workspaceId, contextId, cancellationToken, new SynchronousProgress(report), repoError, levelError,
                repoIdsToUpdate: null,
                commitMessage: start.CommitMessage,
                includeDepsInCommitMessage: start.IncludeDepsInCommitMessage,
                maxLevel: start.MaxLevel,
                runId: runId);

            if (!update.Success)
                return Finish(start, runId, UpdateAndPushStatus.UpdateFailed, "Update stopped. Nothing was pushed.", update: update);

            var plan = await pushOperations.GetPlanAsync(workspaceId, contextId, start.MaxLevel, cancellationToken);
            if (!plan.HasUnpushed || plan.RepositoryIds.Count == 0)
                return Finish(start, runId, UpdateAndPushStatus.NothingToPush, NothingToPushSummary(start.MaxLevel), update: update);

            try
            {
                var push = await pushOperations.PushAsync(
                    workspaceId, contextId, plan.RepositoryIds, synchronizedPush: true, plan.RequiredPackageIds,
                    new SynchronousProgress(report), update.SyncedRepoIds, cancellationToken, runId, start.RestorePackages);
                return FromSequentialPush(start, runId, update, push, plan.RepositoryIds.Count);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                var approvalId = approvals.Add(runId, workspaceId, contextId, start.MaxLevel, start.RestorePackages, plan.RepositoryIds, update.SyncedRepoIds);
                var summary = $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?";
                logger.LogWarning("[UpdateAndPush {RunId}] Workspace {WorkspaceId}: synchronized push not possible ({Missing} missing package mappings); waiting for approval.", runId, workspaceId, ex.MissingPackagesCount);
                return Finish(start, runId, UpdateAndPushStatus.ApprovalRequired, summary, update: update, pendingApprovalId: approvalId);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[UpdateAndPush {RunId}] Workspace {WorkspaceId}: cancelled.", runId, workspaceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UpdateAndPush {RunId}] Workspace {WorkspaceId}: failed with an exception.", runId, workspaceId);
            recorder.Level(0, ex.Message);
            throw;
        }
    }

    private async Task<UpdateAndPushExecutionResult> ContinueWithNormalPushAsync(
        ApproveNormalPushContinuation approval,
        UpdateAndPushObserver? observer,
        CancellationToken cancellationToken)
    {
        var pending = approvals.TryConsume(approval.WorkspaceId, approval.ContextId, approval.PendingApprovalId);
        if (pending == null)
        {
            logger.LogWarning("[UpdateAndPush] Workspace {WorkspaceId} context {ContextId}: normal push approval is unknown, used, expired or for another context.", approval.WorkspaceId, approval.ContextId.Value);
            return Rejected(approval, "none", "The approval is no longer valid. Run Update and Push again.");
        }

        var runId = pending.RunId;
        var workspaceId = approval.WorkspaceId;
        var contextId = approval.ContextId;
        var recorder = outcomes.Begin(workspaceId, contextId, OperationName, runId);
        Action<string> report = message => observer?.ReportProgress?.Invoke(message);

        try
        {
            // Never reuse the old plan: push only what is still pending and approved.
            var plan = await pushOperations.GetPlanAsync(workspaceId, contextId, pending.MaxLevel, cancellationToken);
            var repositoryIds = plan.RepositoryIds.Where(pending.RepositoryIds.Contains).ToHashSet();
            if (!plan.HasUnpushed || repositoryIds.Count == 0)
                return Finish(approval, runId, UpdateAndPushStatus.NothingToPush, NothingToPushSummary(pending.MaxLevel));

            var push = await pushOperations.PushAsync(
                workspaceId, contextId, repositoryIds, synchronizedPush: false, plan.RequiredPackageIds,
                new SynchronousProgress(report), pending.SyncedRepoIds, cancellationToken, runId, pending.RestorePackages);

            if (!push.Success)
                return Finish(approval, runId, UpdateAndPushStatus.PushFailed, PushOperationResult.Describe(push), push: push);

            if (pending.RestorePackages && pending.SyncedRepoIds.Count > 0)
                await updateOperations.RestoreSyncedPackagesAsync(workspaceId, contextId, pending.SyncedRepoIds, new SynchronousProgress(report), cancellationToken);

            return Finish(approval, runId, UpdateAndPushStatus.Completed, "Update and Push completed.", push: push, pushedRepoCount: repositoryIds.Count);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[UpdateAndPush {RunId}] Workspace {WorkspaceId}: normal push cancelled.", runId, workspaceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UpdateAndPush {RunId}] Workspace {WorkspaceId}: normal push failed with an exception.", runId, workspaceId);
            recorder.Level(0, ex.Message);
            throw;
        }
    }

    private UpdateAndPushExecutionResult FromPipeline(StartUpdateAndPush start, string runId, UpdateAndPushResult pipeline)
    {
        if (!pipeline.Update.Success)
            return Finish(start, runId, UpdateAndPushStatus.UpdateFailed, "Update stopped. Levels already handed to the push lane may have been pushed.", pipelined: true, update: pipeline.Update, push: pipeline.Push, pushedRepoCount: pipeline.PushedRepoCount);

        if (pipeline.Push is { Success: false } failure)
            return Finish(start, runId, UpdateAndPushStatus.PushFailed, PushOperationResult.Describe(failure), pipelined: true, update: pipeline.Update, push: failure, pushedRepoCount: pipeline.PushedRepoCount);

        return pipeline.PushedRepoCount == 0
            ? Finish(start, runId, UpdateAndPushStatus.NothingToPush, NothingToPushSummary(start.MaxLevel), pipelined: true, update: pipeline.Update, push: pipeline.Push)
            : Finish(start, runId, UpdateAndPushStatus.Completed, "Update and Push completed.", pipelined: true, update: pipeline.Update, push: pipeline.Push, pushedRepoCount: pipeline.PushedRepoCount);
    }

    private UpdateAndPushExecutionResult FromSequentialPush(
        StartUpdateAndPush start, string runId, DependencyUpdateRunResult update, OperationResult push, int pushedCount)
        => push.Success
            ? Finish(start, runId, UpdateAndPushStatus.Completed, "Update and Push completed.", update: update, push: push, pushedRepoCount: pushedCount)
            : Finish(start, runId, UpdateAndPushStatus.PushFailed, PushOperationResult.Describe(push), update: update, push: push);

    private UpdateAndPushExecutionResult Finish(
        UpdateAndPushCommand command,
        string runId,
        UpdateAndPushStatus status,
        string summary,
        bool pipelined = false,
        DependencyUpdateRunResult? update = null,
        OperationResult? push = null,
        int pushedRepoCount = 0,
        string? pendingApprovalId = null)
    {
        var result = new UpdateAndPushExecutionResult(
            runId, command.WorkspaceId, command.ContextId, status, summary, pipelined, update, push, pushedRepoCount, pendingApprovalId);
        logger.LogInformation(
            "[UpdateAndPush {RunId}] Workspace {WorkspaceId} context {ContextId}: finished. Status={Status}, Pipelined={Pipelined}, UpdateSuccess={UpdateSuccess}, PushSuccess={PushSuccess}, PushedRepoCount={PushedRepoCount}",
            runId, command.WorkspaceId, command.ContextId.Value, status, pipelined, update?.Success, push?.Success, pushedRepoCount);
        return result;
    }

    private UpdateAndPushExecutionResult Rejected(UpdateAndPushCommand command, string runId, string summary)
        => new(runId, command.WorkspaceId, command.ContextId, UpdateAndPushStatus.Rejected, summary);

    private static string NothingToPushSummary(int? maxLevel)
        => maxLevel is { } level ? $"Up to Level {level} updated. Nothing to push." : "Update complete. Nothing to push.";
}
