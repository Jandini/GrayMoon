using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Remove Feature, safe first. Every repository is removed on its own: a clean worktree goes, and one that is not
/// safe to remove (uncommitted work, a lock, an unfinished merge, a commit no branch holds, a nested repository) is
/// kept and reported with its reason, without stopping the others. Nothing is forced unless the user reviewed the
/// live details of that repository and authorized it there. Each step (worktree, local branch, remote branch) has
/// its own outcome, stored on the repository's row after it ran, so a retry continues with what is not resolved.
/// </summary>
public sealed partial class WorkspaceFeatureOperations
{
    /// <summary>Shown for a repository that was clean in the reviewed details and has uncommitted work when the removal runs.</summary>
    internal const string ChangedSinceCheckMessage = "changed since the check, run Remove again.";

    /// <summary>Appended to the result when the Workspace repository's worktree waits for the repositories inside it.</summary>
    internal const string RootKeptMessage = "Workspace repository worktree kept until all repository worktrees are removed.";

    /// <summary>
    /// True when the reviewed details showed uncommitted, staged or conflicted work in this repository, so the user's
    /// "discard uncommitted" choice covers it. A repository that was not reviewed is not covered.
    /// </summary>
    internal static bool RemovePlanShowedUncommittedWork(RemoveFeatureRepositoryPlan? planRow) =>
        planRow is not null && (planRow.HasUncommittedChanges || planRow.HasStagedChanges || planRow.HasConflicts);

    public async Task<RemoveFeatureSummary> GetRemoveFeatureSummaryAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return new RemoveFeatureSummary { Success = false, Error = "Cannot remove the special Workspace context." };

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Include(r => r.WorkspaceRepository)!.ThenInclude(l => l!.Repository)
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .OrderBy(r => r.WorkspaceFeatureRepositoryId)
            .ToListAsync(cancellationToken);
        var pushedWrIds = (await db.WorkspaceRepositoryContextStates
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value && s.BranchHasUpstream == true)
                .Select(s => s.WorkspaceRepositoryId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var changedWrIds = (await db.WorkspaceGitContextRepositoryStatuses
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value
                    && (s.StagedCount > 0 || s.ChangedCount > 0 || s.ConflictCount > 0))
                .Select(s => s.WorkspaceRepositoryId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var live = rows.Where(r => r.State != WorkspaceFeatureRepositoryState.Removed).ToList();
        var pending = new List<RemoveFeaturePendingRepository>();
        foreach (var row in rows)
        {
            var record = FeatureRepositoryRemovalRecord.Parse(row.RemovalOutcomeJson);
            var removed = row.State == WorkspaceFeatureRepositoryState.Removed;
            var step = removed
                ? record.Steps.FirstOrDefault(s => s.Step is RemoveFeatureSteps.LocalBranch or RemoveFeatureSteps.RemoteBranch
                    && s.Status is OperationOutcomeStatus.Failed or OperationOutcomeStatus.Unknown or OperationOutcomeStatus.Cancelled)
                : record.Step(RemoveFeatureSteps.Worktree);
            // A row an older version left in Removing has only its error text.
            var message = removed ? DescribeBranchProblem(record) : step?.Message ?? row.LastError;
            if (step is null && string.IsNullOrWhiteSpace(message))
                continue;
            if (removed && step is null)
                continue;

            pending.Add(new RemoveFeaturePendingRepository(
                row.WorkspaceRepositoryId,
                row.WorkspaceRepository?.Repository?.RepositoryName ?? $"repo-{row.WorkspaceRepositoryId}",
                removed,
                step?.ReasonCode,
                message,
                step?.RequiredAuthorization));
        }

        return new RemoveFeatureSummary
        {
            Success = true,
            FeatureName = info.FeatureName ?? "",
            RepositoryCount = live.Count,
            AlreadyRemovedCount = rows.Count - live.Count,
            FeatureBranchCount = rows.Count(r => !r.IsDetached),
            RecordedPushedBranchCount = rows.Count(r => !r.IsDetached && pushedWrIds.Contains(r.WorkspaceRepositoryId)),
            RecordedChangedRepositoryCount = live.Count(r => changedWrIds.Contains(r.WorkspaceRepositoryId)),
            Pending = pending,
        };
    }

    public async Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return OperationResult.Fail("Cannot remove the special Workspace context.");

        var startedAt = logger.FeatureOperationStarted(
            "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName);
        try
        {
            // The whole removal, including dropping the Feature's rows, takes no Workspace lock and does not
            // synchronize the Workspace. A caller that queues the removal waits for a free slot first.
            var run = await RunRemovalAsync(featureContextId, info, options, progress, cancellationToken);
            var outcome = run.Incomplete
                ?? await FinalizeFeatureRemovalAsync(featureContextId, info, run, cancellationToken);
            logger.FeatureOperationFinished(
                "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt,
                outcome.Success ? "Succeeded" : outcome.Outcome?.Status.ToString() ?? "Failed", outcome.Error);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            logger.FeatureOperationFinished(
                "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt, "Cancelled");
            throw;
        }
    }

    /// <summary>
    /// Removes the worktrees and branches. Deliberately takes no Workspace operation lock: a removal running in the
    /// background must never stop Sync or any other operation from starting. It does not analyze the Feature first:
    /// reviewed details (<see cref="RemoveFeatureOptions.AnalyzedPlan"/>) are only the evidence a discard, an unlock
    /// or a forced branch delete was authorized for, repository by repository.
    /// </summary>
    private async Task<RemovalRun> RunRemovalAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = OperationOutcome.NewOperationId();
        try
        {
            var plan = options.AnalyzedPlan is { Success: true } reviewed ? reviewed : null;
            return await RemoveFeatureCoreAsync(featureContextId, info, options, plan, operationId, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Feature Remove threw. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", info.WorkspaceId, info.FeatureName);
            return RemovalRun.Failed(OperationResult.Fail(ex.Message) with
            {
                Outcome = new OperationOutcome(operationId, RemoveFeatureSteps.OperationName, info.WorkspaceId, featureContextId.Value, [])
                {
                    ContextName = info.FeatureName,
                    OverallStatus = OperationOutcomeStatus.Unknown,
                    OverallReasonCode = OperationReasonCodes.Error,
                    Message = ex.Message,
                },
            });
        }
    }

    private async Task<RemovalRun> RemoveFeatureCoreAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        RemoveFeatureOptions options,
        RemoveFeaturePlan? plan,
        string operationId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var planByWrId = plan?.Repositories.ToDictionary(r => r.WorkspaceRepositoryId) ?? [];

        // The periodic Git Changes sweep must never scan this context's worktrees while they are being deleted
        // below. Disposed in every case (success, failure, cancellation) so monitoring always resumes; the special
        // Workspace context is never paused.
        using var monitoringPause = gitChangesMonitoringPause.Pause(featureContextId.Value);

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);

        List<WorkspaceFeatureRepository> rows;
        Dictionary<int, WorkspaceRepositoryLink> linkByWrId;
        Dictionary<int, bool?> recordedUpstreamByWrId;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var feature = await db.WorkspaceFeatures
                .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);
            var tracked = await db.WorkspaceFeatureRepositories
                .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
                .OrderBy(r => r.WorkspaceFeatureRepositoryId)
                .ToListAsync(cancellationToken);

            // Rows an earlier removal already removed stay Removed: their worktree is never asked for again. Only
            // the rows that are still there move to Removing.
            feature.LifecycleState = WorkspaceFeatureLifecycleState.Removing;
            feature.UpdatedAt = DateTime.UtcNow;
            foreach (var row in tracked.Where(r => r.State != WorkspaceFeatureRepositoryState.Removed))
                row.State = WorkspaceFeatureRepositoryState.Removing;
            await db.SaveChangesAsync(cancellationToken);

            var wrIds = tracked.Select(r => r.WorkspaceRepositoryId).ToList();
            linkByWrId = await db.WorkspaceRepositories
                .AsNoTracking()
                .Include(l => l.Repository)!.ThenInclude(r => r!.Connector)
                .Where(l => wrIds.Contains(l.WorkspaceRepositoryId))
                .ToDictionaryAsync(l => l.WorkspaceRepositoryId, cancellationToken);
            recordedUpstreamByWrId = await db.WorkspaceRepositoryContextStates
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value)
                .ToDictionaryAsync(s => s.WorkspaceRepositoryId, s => s.BranchHasUpstream, cancellationToken);
            rows = tracked;
        }

        var records = new ConcurrentDictionary<int, FeatureRepositoryRemovalRecord>(
            rows.ToDictionary(r => r.WorkspaceRepositoryId, r => FeatureRepositoryRemovalRecord.Parse(r.RemovalOutcomeJson)));
        var removedWrIds = new ConcurrentDictionary<int, byte>(
            rows.Where(r => r.State == WorkspaceFeatureRepositoryState.Removed).ToDictionary(r => r.WorkspaceRepositoryId, _ => (byte)0));
        var worktreeRows = rows.Where(r => !removedWrIds.ContainsKey(r.WorkspaceRepositoryId)).ToList();
        // Removed earlier, with a branch step that did not come to an end: only that step runs again.
        var branchOnlyRows = rows
            .Where(r => removedWrIds.ContainsKey(r.WorkspaceRepositoryId) && HasUnresolvedBranchStep(r, records[r.WorkspaceRepositoryId]))
            .ToList();

        // Before any worktree goes: CodeGraph's index (and a build still running) must not hold the Feature root.
        if (worktreeRows.Count > 0)
            await UninitializeCodeGraphAsync(info.WorkspaceId, featureContextId, progress, cancellationToken);

        var paths = await ResolveRemovalPathsAsync(featureContextId, specialContextId, cancellationToken);
        var context = new RemovalContext(
            featureContextId, specialContextId, info, options, plan, planByWrId, linkByWrId, recordedUpstreamByWrId, paths, records, removedWrIds);

        // The Workspace-role root worktree is removed last, alone, and only when every Source worktree is gone
        // (git worktree remove on a root that still holds Source worktrees would fail or need --force).
        var rootRow = rows.SingleOrDefault(r =>
            linkByWrId.TryGetValue(r.WorkspaceRepositoryId, out var rootLink) && rootLink.Role == WorkspaceRepositoryRole.Workspace);
        var sourceWorktreeRows = worktreeRows.Where(r => r != rootRow).ToList();

        var completed = 0;
        var total = worktreeRows.Count;
        using (var gate = new SemaphoreSlim(MaxParallel))
        {
            async Task RunRowAsync(WorkspaceFeatureRepository row, bool removeWorktree)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (removeWorktree && !await RemoveWorktreeStepAsync(context, row, ReferenceEquals(row, rootRow), cancellationToken))
                        return;
                    await RunBranchStepsAsync(context, row, cancellationToken);
                }
                finally
                {
                    if (removeWorktree)
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress.Report($"Removed feature from {done} of {total} repositories", done, total);
                    }

                    gate.Release();
                }
            }

            // Independent repositories run side by side; one that is blocked does not hold back the others.
            await Task.WhenAll(
                sourceWorktreeRows.Select(r => RunRowAsync(r, removeWorktree: true))
                    .Concat(branchOnlyRows.Select(r => RunRowAsync(r, removeWorktree: false))));

            if (rootRow != null && worktreeRows.Contains(rootRow))
            {
                var sourcesLeft = rows.Count(r => r != rootRow && !removedWrIds.ContainsKey(r.WorkspaceRepositoryId));
                if (sourcesLeft == 0)
                {
                    await RunRowAsync(rootRow, removeWorktree: true);
                }
                else
                {
                    await StoreStepAsync(
                        context, rootRow,
                        OperationStepOutcome.Blocked(
                            RemoveFeatureSteps.Worktree, OperationReasonCodes.WaitingForOtherTargets,
                            sourcesLeft == 1
                                ? "Kept until the 1 repository inside it is removed."
                                : $"Kept until the {sourcesLeft} repositories inside it are removed."),
                        lastError: RootKeptMessage, cancellationToken);
                    progress.Report($"Removed feature from {Interlocked.Increment(ref completed)} of {total} repositories", completed, total);
                }
            }
        }

        // A branch git kept as "not merged" may still be the commit a merged pull request merged (squash and rebase
        // merges are not ancestors of the default branch). Looked up now, and only for the repositories that need it.
        await DeleteBranchesCoveredByMergedPullRequestsAsync(context, rows, cancellationToken);

        var allWorktreesRemoved = rows.All(r => removedWrIds.ContainsKey(r.WorkspaceRepositoryId));
        var unresolvedBranches = rows
            .Where(r => removedWrIds.ContainsKey(r.WorkspaceRepositoryId) && HasUnresolvedBranchStep(r, records[r.WorkspaceRepositoryId]))
            .ToList();

        // Every worktree is unregistered. Anything still on disk (a file or folder some program has open) is deleted
        // here if it has let go, or marked pending deletion for the background cleanup; Remove itself never waits on
        // it. Always asked, also without reported leftovers: the Worker deletes the Feature root only when it is
        // empty, so a file outside every worktree (codegraph.json, .codegraph) would leave the folder behind and
        // block the next Create of the same name. The Worker's cleaner re-checks the folder and refuses anything
        // that is not this Feature's folder under the managed root.
        string? pendingDeletionFolder = null;
        if (allWorktreesRemoved)
        {
            pendingDeletionFolder = await CleanupFeatureFolderForNameAsync(
                info.WorkspaceId, info.FeatureName, onlyIfMarked: false, cancellationToken);
        }

        var report = rows
            .Where(r => removedWrIds.ContainsKey(r.WorkspaceRepositoryId))
            .Select(r => BuildReport(r, linkByWrId, records[r.WorkspaceRepositoryId]))
            .ToList();
        var outcome = BuildOutcome(operationId, info, featureContextId, rows, linkByWrId, records);

        if (allWorktreesRemoved && unresolvedBranches.Count == 0)
            return new RemovalRun(null, report, pendingDeletionFolder, specialContextId, outcome);

        var summary = DescribeIncompleteRemoval(rows, linkByWrId, records, removedWrIds.Count, rootRow);
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // The Feature still owns worktrees or branches that were asked to go: keep everything for a retry.
            var feature = await db.WorkspaceFeatures
                .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);
            feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            feature.LastError = summary;
            feature.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return RemovalRun.Failed(OperationResult.Fail(summary) with
        {
            RemoveFeatureReport = report,
            RemoveFeaturePendingDeletionFolder = pendingDeletionFolder,
            Outcome = outcome,
        });
    }

    // ---------------------------------------------------------------- the worktree step

    /// <summary>
    /// Removes one worktree without force, unless the reviewed details showed this repository as changed and the
    /// user authorized the discard. Returns true when the worktree is gone. The outcome is stored on the row.
    /// </summary>
    private async Task<bool> RemoveWorktreeStepAsync(
        RemovalContext context,
        WorkspaceFeatureRepository row,
        bool isRootRow,
        CancellationToken cancellationToken)
    {
        var repoName = context.RepositoryName(row);
        var planRow = context.PlanByWrId.GetValueOrDefault(row.WorkspaceRepositoryId);

        // Force and unlock are consents for what the user saw: only a repository the reviewed details listed as
        // changed (or locked) gets them, and a forced removal is bound to the commit that was reviewed
        // (review 2026-11-10, finding 18). AllowForceDeleteLocalBranches does not imply discard permission.
        var force = context.Options.AllowDiscardUncommitted && RemovePlanShowedUncommittedWork(planRow);
        var unlock = context.Options.AllowUnlockWorktrees && planRow?.IsLocked == true;

        if (workerBridge.GetUnavailableReason() is { } unavailable)
        {
            await StoreStepAsync(
                context, row,
                OperationStepOutcome.Blocked(RemoveFeatureSteps.Worktree, OperationReasonCodes.WorkerUnavailable, unavailable),
                lastError: unavailable, cancellationToken);
            return false;
        }

        var mainPath = await pathResolver.GetRepositoryPathAsync(context.SpecialContextId, row.WorkspaceRepositoryId, cancellationToken);
        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.RemoveGitWorktree,
            new
            {
                mainRepositoryPath = mainPath,
                worktreePath = row.WorktreePath,
                force,
                expectedHeadSha = force ? planRow?.HeadCommit : null,
                // The root removal deletes the directory itself; Worker residue cleanup must not run.
                featureRootPath = isRootRow ? null : context.Paths.FeatureRootPath,
                featureStorageRoot = isRootRow ? null : context.Paths.FeatureStorageRoot,
                unlock,
            },
            cancellationToken);

        var result = response.Data is null
            ? null
            : WorkerResponseJson.DeserializeWorkerResponse<RemoveGitWorktreeResult>(response.Data);

        if (!response.Success && result is null)
        {
            // No answer from the command (a timeout, a dropped connection): git may or may not have run. Ask what is
            // true now instead of assuming either, and never run the removal a second time to find out.
            var registered = await IsWorktreeRegisteredAsync(mainPath, row.WorktreePath, cancellationToken);
            if (registered == false)
            {
                logger.LogWarning(
                    "RemoveGitWorktree gave no answer for {Path} ({Error}), and the worktree is no longer registered: treated as removed. Repository={Repository}",
                    row.WorktreePath, FeatureOperationLog.Redact(response.Error), repoName);
                response = new WorkerCommandResponse(true, null, null);
            }
            else
            {
                var cause = string.IsNullOrWhiteSpace(response.Error) ? "The Worker did not answer." : response.Error.Trim();
                var step = registered == true
                    ? OperationStepOutcome.Failed(RemoveFeatureSteps.Worktree, cause)
                    : OperationStepOutcome.Unknown(
                        RemoveFeatureSteps.Worktree,
                        $"{cause} Whether the folder was removed could not be checked.",
                        OperationFactGroups.WorktreeRegistration | OperationFactGroups.WorkingTree);
                logger.LogWarning(
                    "RemoveGitWorktree gave no answer for {Path}: {Error}. StillRegistered={Registered} Repository={Repository} FeatureName={FeatureName}",
                    row.WorktreePath, FeatureOperationLog.Redact(response.Error), registered, repoName, context.Info.FeatureName);
                await StoreStepAsync(context, row, step, lastError: step.Message, cancellationToken);
                return false;
            }
        }

        if (!response.Success)
        {
            var refusal = MapWorktreeRefusal(result, response.Error, repoName, row.WorktreePath, reviewed: planRow is not null);
            logger.FeatureRepositoryFailed("Remove", context.Info.WorkspaceId, context.Info.FeatureName, repoName, refusal.Message ?? refusal.ReasonCode);
            await StoreStepAsync(context, row, refusal, lastError: refusal.Message, cancellationToken);
            return false;
        }

        // The worktree is unregistered now, whatever happens to its branch or leftover files below. Stored at once
        // with the row's own short-lived context, so a crash before the Feature is finished keeps it removed.
        // When the worktree was not on its Feature branch, the report names the branch that is still there (or a
        // detached commit), so it never only says "removed". From the reviewed details, else from the inventory the
        // Worker read right before it removed the worktree.
        var keptBranchName = planRow is { IsOffFeatureBranch: true }
            ? planRow.CheckedOutBranch ?? "(detached commit)"
            : planRow is null && !row.IsDetached && result is { HeadSha.Length: > 0 }
                && !string.Equals(result.Branch, context.Info.FeatureName, StringComparison.Ordinal)
                ? result.Branch ?? "(detached commit)"
                : null;
        var record = context.Records[row.WorkspaceRepositoryId] with
        {
            KeptBranchName = keptBranchName,
            ResidueRemaining = result?.ResidueRemaining ?? false,
            ResidueFileCount = result?.ResidueFileCount ?? 0,
            ResidueSampleFiles = result?.ResidueSampleFiles,
            ResidueMessage = result?.ResidueMessage,
        };
        record = record.With(result is { AlreadyRemoved: true }
            ? OperationStepOutcome.NoOp(RemoveFeatureSteps.Worktree, "The folder was already removed.", OperationFactGroups.WorktreeRegistration)
            : OperationStepOutcome.Succeeded(RemoveFeatureSteps.Worktree, observed: OperationFactGroups.WorktreeRegistration));
        if (record.ResidueRemaining)
        {
            // Not a failure: the folder is marked for deletion and cleaned up later.
            record = record.With(new OperationStepOutcome(
                RemoveFeatureSteps.Residue, OperationOutcomeStatus.Skipped, OperationReasonCodes.PathInUse, record.ResidueMessage));
        }

        context.Records[row.WorkspaceRepositoryId] = record;
        context.RemovedWrIds[row.WorkspaceRepositoryId] = 0;
        await PersistRowAsync(row, record, WorkspaceFeatureRepositoryState.Removed, lastError: null, cancellationToken);
        logger.FeatureRepositoryDone("Remove", context.Info.WorkspaceId, context.Info.FeatureName, repoName, "WorktreeRemoved");
        return true;
    }

    /// <summary>
    /// Turns the Worker's refusal into a step outcome. The kinds that say work is at stake come from the Worker's
    /// evidence (its error code), never from message text; each of them keeps the worktree.
    /// </summary>
    private static OperationStepOutcome MapWorktreeRefusal(
        RemoveGitWorktreeResult? refusal, string? responseError, string repoName, string? worktreePath, bool reviewed)
    {
        var detail = FirstNonBlank(refusal?.ErrorMessage, responseError) ?? $"Failed to remove worktree {worktreePath}.";
        const string step = RemoveFeatureSteps.Worktree;
        var observed = OperationFactGroups.WorktreeRegistration | OperationFactGroups.WorkingTree;
        return refusal?.FailureKind switch
        {
            "UncommittedChanges" => OperationStepOutcome.Blocked(
                step, OperationReasonCodes.UncommittedChanges,
                // Reviewed and shown clean: the changes are newer than the review.
                reviewed
                    ? string.IsNullOrWhiteSpace(repoName) ? ChangedSinceCheckMessage : $"{repoName}: {ChangedSinceCheckMessage}"
                    : "It has uncommitted changes, so the folder was kept. Commit them, or review the details to discard them.",
                OperationAuthorizations.DiscardUncommittedChanges) with { Observed = observed },
            "WorktreeLocked" => OperationStepOutcome.Blocked(
                step, OperationReasonCodes.WorktreeLocked, $"{detail.TrimEnd('.')}. Unlock it, or review the details to unlock it here.",
                OperationAuthorizations.UnlockWorktree) with { Observed = OperationFactGroups.WorktreeRegistration },
            "OperationInProgress" => OperationStepOutcome.Blocked(
                step, OperationReasonCodes.OperationInProgress, detail, OperationAuthorizations.DiscardUncommittedChanges) with { Observed = observed },
            "UnpreservedCommits" => OperationStepOutcome.Blocked(step, OperationReasonCodes.UnpreservedCommits, detail) with { Observed = observed },
            "NestedRepository" => OperationStepOutcome.Blocked(step, OperationReasonCodes.NestedRepository, detail) with { Observed = observed },
            "ChangedSinceReview" => OperationStepOutcome.Blocked(step, OperationReasonCodes.ChangedSinceReview, detail) with { Observed = observed },
            "PathInUse" => OperationStepOutcome.Failed(step, detail, OperationReasonCodes.PathInUse),
            "AccessDenied" => OperationStepOutcome.Failed(step, detail, OperationReasonCodes.AccessDenied),
            _ => OperationStepOutcome.Failed(step, detail),
        };
    }

    /// <summary>
    /// Whether the worktree is still in the repository's worktree list: true, false, or null when the list could not
    /// be read. One read of one repository; it changes nothing.
    /// </summary>
    private async Task<bool?> IsWorktreeRegisteredAsync(string? mainRepositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath))
            return null;

        try
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.ListGitWorktrees, new { mainRepositoryPath }, cancellationToken);
            if (!response.Success || response.Data is null)
                return null;

            var payload = WorkerResponseJson.DeserializeWorkerResponse<ListWorktreesWorkerResponse>(response.Data);
            if (payload?.Worktrees is null)
                return null;
            return payload.Worktrees.Any(wt => WorkspaceFeatureReconciler.PathsEqualNormalized(wt.WorktreePath, worktreePath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ListGitWorktrees failed while checking {Path}", worktreePath);
            return null;
        }
    }

    // ---------------------------------------------------------------- the branch steps

    /// <summary>A row whose worktree is gone and whose branch work was asked for and did not come to an end.</summary>
    private static bool HasUnresolvedBranchStep(WorkspaceFeatureRepository row, FeatureRepositoryRemovalRecord record)
    {
        if (row.IsDetached)
            return false;
        return record.Steps.Any(s =>
            s.Step is RemoveFeatureSteps.LocalBranch or RemoveFeatureSteps.RemoteBranch
            && s.Status is OperationOutcomeStatus.Failed or OperationOutcomeStatus.Unknown or OperationOutcomeStatus.Cancelled);
    }

    /// <summary>
    /// After the worktree is gone: the local Feature branch, then the remote one. Branch deletion is its own
    /// decision, independent of the worktree: a kept branch never brings the worktree back, and a branch that could
    /// not be deleted is retried without touching anything else. A step that already came to an end is not run again.
    /// </summary>
    private async Task RunBranchStepsAsync(RemovalContext context, WorkspaceFeatureRepository row, CancellationToken cancellationToken)
    {
        var record = context.Records[row.WorkspaceRepositoryId];
        var repoName = context.RepositoryName(row);

        // Tag-pinned repositories and read only snapshots never got a Feature branch; a same-named branch is not ours.
        var branchName = row.IsDetached ? null : context.Info.FeatureName;
        if (string.IsNullOrWhiteSpace(branchName))
        {
            if (record.Step(RemoveFeatureSteps.LocalBranch) is null)
            {
                record = record
                    .With(OperationStepOutcome.Skipped(RemoveFeatureSteps.LocalBranch, OperationReasonCodes.NotApplicable))
                    .With(OperationStepOutcome.Skipped(RemoveFeatureSteps.RemoteBranch, OperationReasonCodes.NotApplicable));
                await StoreRecordAsync(context, row, record, cancellationToken);
            }

            return;
        }

        var canAddress = context.LinkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link)
            && !string.IsNullOrWhiteSpace(link.Repository?.RepositoryName)
            && !string.IsNullOrWhiteSpace(context.Paths.WorkspaceRoot)
            && !string.IsNullOrWhiteSpace(context.Paths.WorkspaceFolderName);
        var planRow = context.PlanByWrId.GetValueOrDefault(row.WorkspaceRepositoryId);

        if (record.IsUnresolved(RemoveFeatureSteps.LocalBranch))
        {
            record = await RunLocalBranchStepAsync(context, row, record, branchName, repoName, canAddress, planRow, cancellationToken);
            await StoreRecordAsync(context, row, record, cancellationToken);
        }

        if (record.IsUnresolved(RemoveFeatureSteps.RemoteBranch))
        {
            record = await RunRemoteBranchStepAsync(context, row, record, branchName, repoName, canAddress ? link : null, planRow, cancellationToken);
            await StoreRecordAsync(context, row, record, cancellationToken);
        }
    }

    private async Task<FeatureRepositoryRemovalRecord> RunLocalBranchStepAsync(
        RemovalContext context,
        WorkspaceFeatureRepository row,
        FeatureRepositoryRemovalRecord record,
        string branchName,
        string repoName,
        bool canAddress,
        RemoveFeatureRepositoryPlan? planRow,
        CancellationToken cancellationToken)
    {
        const string step = RemoveFeatureSteps.LocalBranch;
        if (!context.Options.DeleteLocalBranches)
            return record.With(OperationStepOutcome.Skipped(step, OperationReasonCodes.NotRequested, "Local Feature branch kept (delete not selected)."));

        if (!canAddress)
        {
            // Never silently skipped: the repository's local branch may still exist.
            logger.LogWarning(
                "Could not resolve repository details to delete local Feature branch {Branch} for WorkspaceRepository {WorkspaceRepositoryId}.",
                branchName, row.WorkspaceRepositoryId);
            return record.With(OperationStepOutcome.Failed(step, "Could not resolve repository details to delete the local branch."));
        }

        // A forced delete needs evidence for this branch at this commit: the user authorized it for a repository the
        // reviewed details listed as having commits that are not merged, or a merged pull request merged exactly this
        // tip (squash and rebase merges are not ancestors of the default branch). It is leased on the reviewed tip.
        var authorized = context.Options.AllowForceDeleteLocalBranches
            && planRow is not null
            && planRow.NeedsForceDeleteAuthorization(context.Plan!.PullRequestStatusUnknown);
        var covered = planRow?.LocalBranchCoveredByMergedPullRequest == true && !context.Plan!.PullRequestStatusUnknown;
        var force = (authorized || covered) && !string.IsNullOrWhiteSpace(planRow?.FeatureBranchSha);

        var delete = await DeleteBranchAsync(
            context, repoName, branchName, isRemote: false, force, expectedSha: force ? planRow!.FeatureBranchSha : null, bearerToken: null, cancellationToken);
        record = record with
        {
            BranchSha = delete.Payload?.BranchSha ?? record.BranchSha ?? planRow?.FeatureBranchSha,
            BranchHasUpstream = delete.Payload?.HasUpstream ?? record.BranchHasUpstream ?? planRow?.FeatureBranchHasUpstream,
        };

        if (delete.Success)
        {
            await RemovePersistedBranchAsync(row.WorkspaceRepositoryId, branchName, isRemote: false, cancellationToken);
            return record.With(OperationStepOutcome.Succeeded(step, observed: OperationFactGroups.LocalBranches));
        }

        logger.LogWarning(
            "Delete local Feature branch {Branch} was not done for {Repo}: {Kind} {Error}",
            branchName, repoName, delete.Payload?.FailureKind, FeatureOperationLog.Redact(delete.Error));
        return record.With(delete.Payload?.FailureKind switch
        {
            "NotFound" => OperationStepOutcome.NoOp(step, "The local Feature branch was already gone.", OperationFactGroups.LocalBranches),
            "NotFullyMerged" => OperationStepOutcome.Blocked(
                step, OperationReasonCodes.UnmergedCommits,
                "The local branch has commits that are not in the default branch, so it was kept.",
                OperationAuthorizations.ForceDeleteLocalBranch, canRetry: false) with { Observed = OperationFactGroups.LocalBranches },
            "TipChanged" => OperationStepOutcome.Blocked(
                step, OperationReasonCodes.ChangedSinceReview, delete.Error, canRetry: false) with { Observed = OperationFactGroups.LocalBranches },
            "CheckedOut" => OperationStepOutcome.Blocked(step, OperationReasonCodes.PolicyExcluded, delete.Error, canRetry: false),
            // No answer from the command: the branch may or may not be gone. The retry asks git again, which is safe
            // for a non-force delete and leased for a forced one.
            null when delete.Payload is null => OperationStepOutcome.Unknown(
                step, delete.Error ?? "The Worker did not answer.", OperationFactGroups.LocalBranches),
            _ => OperationStepOutcome.Failed(step, delete.Error ?? "Local branch delete failed."),
        });
    }

    private async Task<FeatureRepositoryRemovalRecord> RunRemoteBranchStepAsync(
        RemovalContext context,
        WorkspaceFeatureRepository row,
        FeatureRepositoryRemovalRecord record,
        string branchName,
        string repoName,
        WorkspaceRepositoryLink? link,
        RemoveFeatureRepositoryPlan? planRow,
        CancellationToken cancellationToken)
    {
        const string step = RemoveFeatureSteps.RemoteBranch;

        // Whether the branch was ever pushed: the reviewed details, else what the local delete read, else what
        // GrayMoon last recorded for this Feature. Not pushed means there is nothing to delete.
        var hasUpstream = planRow?.FeatureBranchHasUpstream
            ?? record.BranchHasUpstream
            ?? context.RecordedUpstreamByWrId.GetValueOrDefault(row.WorkspaceRepositoryId);
        if (hasUpstream != true)
            return record.With(OperationStepOutcome.Skipped(step, OperationReasonCodes.NotApplicable));

        if (!context.Options.DeleteRemoteBranches)
            return record.With(OperationStepOutcome.Skipped(step, OperationReasonCodes.NotRequested, "Remote Feature branch kept (delete not selected)."));

        if (link is null)
        {
            logger.FeatureRepositoryFailed(
                "Remove", context.Info.WorkspaceId, context.Info.FeatureName, repoName, "Could not resolve repository details to delete the remote branch.");
            return record.With(OperationStepOutcome.Failed(step, "Could not resolve repository details to delete the remote branch."));
        }

        var defaultBranch = link.DefaultBranchName;
        if (!string.IsNullOrWhiteSpace(defaultBranch) && branchName.Equals(defaultBranch.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return record.With(OperationStepOutcome.Blocked(
                step, OperationReasonCodes.PolicyExcluded, "Refused to delete the repository default branch.", canRetry: false));
        }

        // Without reviewed details nobody saw what this branch holds. Its remote copy goes only when the local
        // branch was deleted as merged (git, or a merged pull request, vouched for the commits); a branch that was
        // kept keeps its remote copy too.
        var local = record.Step(RemoveFeatureSteps.LocalBranch);
        if (planRow is null && local is not { Status: OperationOutcomeStatus.Succeeded })
        {
            return record.With(OperationStepOutcome.Blocked(
                step, OperationReasonCodes.UnmergedCommits,
                "Remote branch kept because the local branch was not deleted as merged. Review the details to delete it.",
                OperationAuthorizations.DeleteRemoteBranch, canRetry: false));
        }

        // The lease: the remote tip must be the commit this Feature's branch had.
        var expectedSha = FirstNonBlank(planRow?.FeatureBranchSha, record.BranchSha);
        if (string.IsNullOrWhiteSpace(expectedSha))
        {
            logger.FeatureRepositoryFailed(
                "Remove", context.Info.WorkspaceId, context.Info.FeatureName, repoName, "Could not confirm the Feature branch tip for a safe remote delete.");
            return record.With(OperationStepOutcome.Blocked(
                step, OperationReasonCodes.PrerequisiteNotMet,
                "Could not confirm the Feature branch tip for a safe remote delete.", canRetry: false));
        }

        var bearerToken = ConnectorHelpers.UnprotectToken(link.Repository?.Connector?.UserToken);
        var delete = await DeleteBranchAsync(context, repoName, branchName, isRemote: true, force: false, expectedSha, bearerToken, cancellationToken);
        if (delete.Success)
        {
            await RemovePersistedBranchAsync(row.WorkspaceRepositoryId, branchName, isRemote: true, cancellationToken);
            return record.With(delete.Payload?.AlreadyAbsent == true
                ? OperationStepOutcome.NoOp(step, "The remote branch was already gone.", OperationFactGroups.RemoteRefs)
                : OperationStepOutcome.Succeeded(step, observed: OperationFactGroups.RemoteRefs));
        }

        var error = string.IsNullOrWhiteSpace(delete.Error) ? "Remote branch delete failed." : delete.Error;
        logger.LogWarning(
            "Delete remote Feature branch {Branch} was not done for {Repo}: {Kind} {Error}",
            branchName, repoName, delete.Payload?.FailureKind, FeatureOperationLog.Redact(delete.Error));
        return record.With(delete.Payload?.FailureKind switch
        {
            "LeaseRefused" => OperationStepOutcome.Blocked(step, OperationReasonCodes.LeaseRefused, error, canRetry: false) with { Observed = OperationFactGroups.RemoteRefs },
            "Protected" => OperationStepOutcome.Blocked(step, OperationReasonCodes.PolicyExcluded, error, canRetry: false),
            null when delete.Payload is null => OperationStepOutcome.Unknown(step, error, OperationFactGroups.RemoteRefs),
            _ => OperationStepOutcome.Failed(step, error, OperationReasonCodes.RemoteUnavailable),
        });
    }

    /// <summary>
    /// For repositories whose local branch git kept as not merged, without reviewed details: one pull request
    /// refresh for exactly those repositories. A branch whose tip is the head of a merged pull request is deleted
    /// (leased on that tip), and then its remote copy. Everything else stays kept.
    /// </summary>
    private async Task DeleteBranchesCoveredByMergedPullRequestsAsync(
        RemovalContext context,
        IReadOnlyList<WorkspaceFeatureRepository> rows,
        CancellationToken cancellationToken)
    {
        if (!context.Options.DeleteLocalBranches)
            return;

        var candidates = rows
            .Where(r => !r.IsDetached
                && context.RemovedWrIds.ContainsKey(r.WorkspaceRepositoryId)
                && !context.PlanByWrId.ContainsKey(r.WorkspaceRepositoryId)
                && context.Records[r.WorkspaceRepositoryId] is { BranchSha: { Length: > 0 } } record
                && record.Step(RemoveFeatureSteps.LocalBranch) is { Status: OperationOutcomeStatus.Blocked, ReasonCode: OperationReasonCodes.UnmergedCommits }
                && context.LinkByWrId.ContainsKey(r.WorkspaceRepositoryId))
            .ToList();
        if (candidates.Count == 0)
            return;

        var branchByRepositoryId = candidates.ToDictionary(
            r => context.LinkByWrId[r.WorkspaceRepositoryId].RepositoryId, _ => context.Info.FeatureName);
        var mergedHeadShaByRepositoryId = new Dictionary<int, string?>();
        IReadOnlyDictionary<int, PullRequestRefreshOutcome> refreshed;
        try
        {
            refreshed = await workspacePullRequestService.RefreshContextPullRequestsAsync(
                context.Info.WorkspaceId, context.FeatureContextId.Value, branchByRepositoryId, force: false, cancellationToken,
                mergedHeadShaByRepositoryId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown is not merged: the branches stay kept, as git left them.
            logger.LogWarning(ex, "Pull request refresh failed while removing Feature {FeatureName}; branches that are not merged stay kept.", context.Info.FeatureName);
            return;
        }

        HashSet<int> mergedWrIds;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var wrIds = candidates.Select(r => r.WorkspaceRepositoryId).ToList();
            mergedWrIds = (await db.WorkspaceRepositoryContextPullRequests
                    .AsNoTracking()
                    .Where(p => p.WorkspaceFeatureContextId == context.FeatureContextId.Value
                        && wrIds.Contains(p.WorkspaceRepositoryId)
                        && p.MergedAt != null)
                    .Select(p => p.WorkspaceRepositoryId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(candidates.Select(async row =>
        {
            var repositoryId = context.LinkByWrId[row.WorkspaceRepositoryId].RepositoryId;
            var record = context.Records[row.WorkspaceRepositoryId];
            if (!mergedWrIds.Contains(row.WorkspaceRepositoryId)
                || refreshed.GetValueOrDefault(repositoryId) == PullRequestRefreshOutcome.Failed
                || !mergedHeadShaByRepositoryId.TryGetValue(repositoryId, out var mergedHead)
                || !string.Equals(mergedHead, record.BranchSha, StringComparison.OrdinalIgnoreCase))
                return;

            await gate.WaitAsync(cancellationToken);
            try
            {
                var repoName = context.RepositoryName(row);
                var delete = await DeleteBranchAsync(
                    context, repoName, context.Info.FeatureName!, isRemote: false, force: true, expectedSha: record.BranchSha, bearerToken: null, cancellationToken);
                if (!delete.Success)
                {
                    logger.LogWarning(
                        "Delete of local Feature branch {Branch} covered by a merged pull request was not done for {Repo}: {Error}",
                        context.Info.FeatureName, repoName, FeatureOperationLog.Redact(delete.Error));
                    return;
                }

                await RemovePersistedBranchAsync(row.WorkspaceRepositoryId, context.Info.FeatureName!, isRemote: false, cancellationToken);
                record = record
                    .With(OperationStepOutcome.Succeeded(
                        RemoveFeatureSteps.LocalBranch, "Deleted: a merged pull request merged this commit.", OperationFactGroups.LocalBranches | OperationFactGroups.PullRequest));
                // The remote copy was kept only because the local branch was; decide it again.
                record = record with { Steps = record.Steps.Where(s => s.Step != RemoveFeatureSteps.RemoteBranch).ToList() };
                context.Records[row.WorkspaceRepositoryId] = record;
                await StoreRecordAsync(context, row, record, cancellationToken);
                await RunBranchStepsAsync(context, row, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async Task<(bool Success, string? Error, DeleteBranchWorkerResult? Payload)> DeleteBranchAsync(
        RemovalContext context,
        string repoName,
        string branchName,
        bool isRemote,
        bool force,
        string? expectedSha,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var response = await workerBridge.SendCommandAsync(
            "DeleteBranch",
            new
            {
                workspaceName = context.Paths.WorkspaceFolderName,
                repositoryName = repoName,
                workspaceRepositoryName = context.Paths.WorkspaceRepositoryName,
                branchName,
                isRemote,
                force,
                bearerToken,
                expectedSha,
                workspaceRoot = context.Paths.WorkspaceRoot,
                // The branch row is removed by this removal. A hook here would queue a sync of the main checkout
                // for every repository, for state this removal did not change.
                skipHooks = true,
            },
            cancellationToken);
        var payload = response.Data is null
            ? null
            : WorkerResponseJson.DeserializeWorkerResponse<DeleteBranchWorkerResult>(response.Data);
        return (response.Success, FirstNonBlank(payload?.ErrorMessage, response.Error), payload);
    }

    // ---------------------------------------------------------------- storing and reporting

    private Task StoreRecordAsync(
        RemovalContext context, WorkspaceFeatureRepository row, FeatureRepositoryRemovalRecord record, CancellationToken cancellationToken)
    {
        context.Records[row.WorkspaceRepositoryId] = record;
        return PersistRowAsync(row, record, state: null, lastError: DescribeBranchProblem(record), cancellationToken);
    }

    /// <summary>Stores the outcome of a step that left the worktree in place: the row keeps its state and gets the reason.</summary>
    private Task StoreStepAsync(
        RemovalContext context, WorkspaceFeatureRepository row, OperationStepOutcome step, string? lastError, CancellationToken cancellationToken)
    {
        var record = context.Records[row.WorkspaceRepositoryId].With(step);
        context.Records[row.WorkspaceRepositoryId] = record;
        return PersistRowAsync(row, record, state: null, lastError, cancellationToken);
    }

    /// <summary>
    /// Writes one row with its own short-lived context (rows are processed side by side, and a DbContext is not
    /// thread-safe). Uses no cancellation for the write itself once the step ran: what git did must be recorded.
    /// </summary>
    private async Task PersistRowAsync(
        WorkspaceFeatureRepository row,
        FeatureRepositoryRemovalRecord record,
        WorkspaceFeatureRepositoryState? state,
        string? lastError,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await using var rowDb = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var tracked = await rowDb.WorkspaceFeatureRepositories.FirstOrDefaultAsync(
            r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, CancellationToken.None);
        if (tracked is null)
            return;

        if (state is { } newState)
            tracked.State = newState;
        tracked.LastError = string.IsNullOrWhiteSpace(lastError) ? null : Truncate(lastError.Trim(), 2000);
        tracked.RemovalOutcomeJson = record.ToJson();
        await rowDb.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The text a removed row shows while a branch step is not resolved; null when its branches are settled.</summary>
    private static string? DescribeBranchProblem(FeatureRepositoryRemovalRecord record)
    {
        if (!record.WorktreeRemoved)
            return record.Step(RemoveFeatureSteps.Worktree)?.Message;

        var problems = record.Steps
            .Where(s => s.Step is RemoveFeatureSteps.LocalBranch or RemoveFeatureSteps.RemoteBranch
                && s.Status is OperationOutcomeStatus.Failed or OperationOutcomeStatus.Unknown or OperationOutcomeStatus.Cancelled)
            .Select(s => $"{(s.Step == RemoveFeatureSteps.LocalBranch ? "Local" : "Remote")} branch was not deleted: {s.Message}".TrimEnd(':', ' '))
            .ToList();
        return problems.Count == 0 ? null : "Folder removed. " + string.Join(" ", problems);
    }

    private static RemoveFeatureRepositoryReport BuildReport(
        WorkspaceFeatureRepository row,
        IReadOnlyDictionary<int, WorkspaceRepositoryLink> linkByWrId,
        FeatureRepositoryRemovalRecord record)
    {
        var local = record.Step(RemoveFeatureSteps.LocalBranch);
        var remote = record.Step(RemoveFeatureSteps.RemoteBranch);
        var branchOutcome = local switch
        {
            null => RemoveFeatureBranchOutcome.NotApplicable,
            { Status: OperationOutcomeStatus.Succeeded } => RemoveFeatureBranchOutcome.Deleted,
            { Status: OperationOutcomeStatus.NoOp } => RemoveFeatureBranchOutcome.NotApplicable,
            { Status: OperationOutcomeStatus.Skipped, ReasonCode: OperationReasonCodes.NotRequested } => RemoveFeatureBranchOutcome.Kept,
            { Status: OperationOutcomeStatus.Skipped } => RemoveFeatureBranchOutcome.NotApplicable,
            { Status: OperationOutcomeStatus.Blocked, ReasonCode: OperationReasonCodes.UnmergedCommits or OperationReasonCodes.ChangedSinceReview }
                => RemoveFeatureBranchOutcome.KeptUnmerged,
            _ => RemoveFeatureBranchOutcome.Failed,
        };
        var remoteOutcome = remote switch
        {
            null => RemoveFeatureRemoteBranchOutcome.NotApplicable,
            { Status: OperationOutcomeStatus.Succeeded or OperationOutcomeStatus.NoOp } => RemoveFeatureRemoteBranchOutcome.Deleted,
            { Status: OperationOutcomeStatus.Skipped, ReasonCode: OperationReasonCodes.NotRequested } => RemoveFeatureRemoteBranchOutcome.Kept,
            { Status: OperationOutcomeStatus.Skipped } => RemoveFeatureRemoteBranchOutcome.NotApplicable,
            { Status: OperationOutcomeStatus.Blocked, ReasonCode: OperationReasonCodes.LeaseRefused } => RemoveFeatureRemoteBranchOutcome.RefusedLease,
            { Status: OperationOutcomeStatus.Blocked, ReasonCode: OperationReasonCodes.UnmergedCommits } => RemoveFeatureRemoteBranchOutcome.Kept,
            _ => RemoveFeatureRemoteBranchOutcome.Failed,
        };
        return new RemoveFeatureRepositoryReport(
            row.WorkspaceRepositoryId,
            linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link) ? link.Repository?.RepositoryName ?? "" : "",
            WorktreeRemoved: true,
            branchOutcome,
            branchOutcome is RemoveFeatureBranchOutcome.Deleted or RemoveFeatureBranchOutcome.NotApplicable ? null : local?.Message,
            record.ResidueRemaining,
            record.ResidueFileCount,
            record.ResidueSampleFiles,
            record.ResidueMessage,
            record.KeptBranchName,
            remoteOutcome,
            remoteOutcome is RemoveFeatureRemoteBranchOutcome.Deleted or RemoveFeatureRemoteBranchOutcome.NotApplicable ? null : remote?.Message);
    }

    private static OperationOutcome BuildOutcome(
        string operationId,
        WorkspaceFeatureContextInfo info,
        WorkspaceFeatureContextId featureContextId,
        IReadOnlyList<WorkspaceFeatureRepository> rows,
        IReadOnlyDictionary<int, WorkspaceRepositoryLink> linkByWrId,
        IReadOnlyDictionary<int, FeatureRepositoryRemovalRecord> records)
    {
        var targets = rows
            .Select(row =>
            {
                linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link);
                var steps = records[row.WorkspaceRepositoryId].Steps;
                return new OperationTargetOutcome(
                    new OperationTarget(
                        OperationTargetKind.Repository,
                        link?.Repository?.RepositoryName ?? $"repo-{row.WorkspaceRepositoryId}",
                        link?.RepositoryId,
                        row.WorkspaceRepositoryId,
                        row.WorktreePath),
                    steps.Count > 0
                        ? steps
                        : [OperationStepOutcome.Cancelled(RemoveFeatureSteps.Worktree, "The removal stopped before it reached this repository.")]);
            })
            .ToList();
        return new OperationOutcome(operationId, RemoveFeatureSteps.OperationName, info.WorkspaceId, featureContextId.Value, targets)
        {
            ContextName = info.FeatureName,
            // Removing worktrees and Feature branches changes nothing the Workspace's derived state is computed from.
            DerivedState = DerivedStateReadiness.NotAffected,
        };
    }

    /// <summary>
    /// One text for the toast, the Feature's error and the result: how many repositories were removed, why the
    /// first ones were not (up to three, by name), and that the Workspace repository waits when it does.
    /// </summary>
    private static string DescribeIncompleteRemoval(
        IReadOnlyList<WorkspaceFeatureRepository> rows,
        IReadOnlyDictionary<int, WorkspaceRepositoryLink> linkByWrId,
        IReadOnlyDictionary<int, FeatureRepositoryRemovalRecord> records,
        int removedCount,
        WorkspaceFeatureRepository? rootRow)
    {
        string Name(WorkspaceFeatureRepository row) =>
            linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link) ? link.Repository?.RepositoryName ?? "" : "";

        var kept = rows
            .Where(r => !records[r.WorkspaceRepositoryId].WorktreeRemoved && r != rootRow)
            .ToList();
        var rootWaits = rootRow is not null
            && records[rootRow.WorkspaceRepositoryId].Step(RemoveFeatureSteps.Worktree)
                is { ReasonCode: OperationReasonCodes.WaitingForOtherTargets };
        var rootKeptForItsOwnReason = rootRow is not null && !records[rootRow.WorkspaceRepositoryId].WorktreeRemoved && !rootWaits;
        if (rootKeptForItsOwnReason)
            kept.Add(rootRow!);

        var parts = new List<string>();
        foreach (var row in kept.Take(3))
        {
            var message = records[row.WorkspaceRepositoryId].Step(RemoveFeatureSteps.Worktree)?.Message ?? "The folder was kept.";
            var name = Name(row);
            // Some messages already start with the repository name.
            parts.Add(string.IsNullOrWhiteSpace(name) || message.StartsWith(name + ":", StringComparison.Ordinal) ? message : $"{name}: {message}");
        }

        if (kept.Count > 3)
            parts.Add(kept.Count - 3 == 1 ? "1 more repository was kept." : $"{kept.Count - 3} more repositories were kept.");

        var branchProblems = rows
            .Where(r => records[r.WorkspaceRepositoryId].WorktreeRemoved && HasUnresolvedBranchStep(r, records[r.WorkspaceRepositoryId]))
            .ToList();
        foreach (var row in branchProblems.Take(3))
            parts.Add($"{Name(row)}: {DescribeBranchProblem(records[row.WorkspaceRepositoryId])}".TrimStart(':', ' '));
        if (branchProblems.Count > 3)
            parts.Add($"{branchProblems.Count - 3} more repositories have a branch that was not deleted.");

        if (rootWaits)
            parts.Add(RootKeptMessage);

        var headline = removedCount > 0
            ? $"Feature removal is incomplete: {removedCount} of {rows.Count} repositories removed."
            : null;
        return string.Join(" ", new[] { headline }.Concat(parts).Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private async Task<RemovalPaths> ResolveRemovalPathsAsync(
        WorkspaceFeatureContextId featureContextId, WorkspaceFeatureContextId specialContextId, CancellationToken cancellationToken)
    {
        string? workspaceRoot = null;
        string? workspaceFolderName = null;
        string? workspaceRepositoryName = null;
        try
        {
            (workspaceRoot, workspaceFolderName, workspaceRepositoryName) =
                await pathResolver.GetWorkerArgsAsync(specialContextId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Workspace worker paths for Feature branch delete.");
        }

        // The Worker's residue cleanup only deletes files when both of these are set; a resolution failure here
        // leaves them null and the leftovers are only reported.
        string? featureRootPath = null;
        string? featureStorageRoot = null;
        try
        {
            featureRootPath = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
            (featureStorageRoot, _, _) = await pathResolver.GetWorkerArgsAsync(featureContextId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Feature storage paths for worktree residue cleanup.");
        }

        return new RemovalPaths(workspaceRoot, workspaceFolderName, workspaceRepositoryName, featureRootPath, featureStorageRoot);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record RemovalPaths(
        string? WorkspaceRoot,
        string? WorkspaceFolderName,
        string? WorkspaceRepositoryName,
        string? FeatureRootPath,
        string? FeatureStorageRoot);

    /// <summary>Everything the steps of one removal run share. The dictionaries are written from several rows at once.</summary>
    private sealed record RemovalContext(
        WorkspaceFeatureContextId FeatureContextId,
        WorkspaceFeatureContextId SpecialContextId,
        WorkspaceFeatureContextInfo Info,
        RemoveFeatureOptions Options,
        RemoveFeaturePlan? Plan,
        IReadOnlyDictionary<int, RemoveFeatureRepositoryPlan> PlanByWrId,
        IReadOnlyDictionary<int, WorkspaceRepositoryLink> LinkByWrId,
        IReadOnlyDictionary<int, bool?> RecordedUpstreamByWrId,
        RemovalPaths Paths,
        ConcurrentDictionary<int, FeatureRepositoryRemovalRecord> Records,
        ConcurrentDictionary<int, byte> RemovedWrIds)
    {
        public string RepositoryName(WorkspaceFeatureRepository row)
            => LinkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link) ? link.Repository?.RepositoryName ?? "" : "";
    }

    /// <summary>What the removal leaves for <see cref="FinalizeFeatureRemovalAsync"/>, or the result of a removal that is not complete.</summary>
    private sealed record RemovalRun(
        OperationResult? Incomplete,
        IReadOnlyList<RemoveFeatureRepositoryReport> Report,
        string? PendingDeletionFolder,
        WorkspaceFeatureContextId SpecialContextId,
        OperationOutcome? Outcome)
    {
        public static RemovalRun Failed(OperationResult result) => new(result, [], null, default, result.Outcome);
    }

    /// <summary>App-side shape of the Worker's RemoveGitWorktree response (GrayMoon.Worker.Jobs.Response is not referenced here).</summary>
    private sealed class RemoveGitWorktreeResult
    {
        public bool Success { get; set; }
        public bool AlreadyRemoved { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool ResidueRemaining { get; set; }
        public int ResidueFileCount { get; set; }
        public List<string>? ResidueSampleFiles { get; set; }
        public string? ResidueMessage { get; set; }
        public string? FailureKind { get; set; }
        public string? Branch { get; set; }
        public string? HeadSha { get; set; }
    }

    /// <summary>App-side shape of the Worker's DeleteBranch response.</summary>
    private sealed class DeleteBranchWorkerResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FailureKind { get; set; }
        public string? BranchSha { get; set; }
        public bool? HasUpstream { get; set; }
        public bool AlreadyAbsent { get; set; }
    }
}
