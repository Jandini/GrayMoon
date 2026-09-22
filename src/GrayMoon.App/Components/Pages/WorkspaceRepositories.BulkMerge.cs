using GrayMoon.App.Components.Modals;
using GrayMoon.App.Models;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Bulk-merge dialog opened per dependency level from the level-header "..." menu's "Merge PRs..." item.
/// Candidates are resolved authoritatively (GetRepositoryIdsAtLevelAsync + GetAllLinksForOperationAsync joined
/// against the persisted PR table), never from the render-cache <c>prByRepositoryId</c> that
/// GetHydratedLinksAtLevel/HasMergeablePr use - virtual scrolling means most rows are not hydrated.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    [Inject] private MergePullRequestReturnToDefaultPreferenceService ReturnToDefaultPreferenceService { get; set; } = default!;

    private BulkMergePullRequestsModalState _bulkMergeModal = new();

    /// <summary>Bound to the same singleton the single-PR dialog's own "Return to default branch" checkbox uses, so the two remember one shared last choice.</summary>
    private bool BulkMergeReturnToDefault
    {
        get => ReturnToDefaultPreferenceService.ReturnToDefault;
        set => ReturnToDefaultPreferenceService.ReturnToDefault = value;
    }

    private async Task OpenMergePullRequestsDialogForLevelAsync(int? levelKey)
    {
        var ids = (await GetRepositoryIdsAtLevelAsync(levelKey)).ToHashSet();
        if (ids.Count == 0)
        {
            ToastService.Show("No open pull requests in this level.");
            return;
        }

        IReadOnlyList<WorkspaceRepositoryLink> links;
        IReadOnlyDictionary<int, PullRequestInfo?> persistedPrs;
        try
        {
            links = await GetAllLinksForOperationAsync();
            persistedPrs = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, IReadOnlyDictionary<int, PullRequestInfo?>>(
                svc => svc.GetPersistedPullRequestsForWorkspaceAsync(WorkspaceId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load pull request candidates for bulk merge, workspace {WorkspaceId}, level {LevelKey}", WorkspaceId, levelKey);
            ToastService.ShowError("Could not load pull requests for this level.");
            return;
        }

        var linkByRepoId = links.Where(wr => ids.Contains(wr.RepositoryId)).ToDictionary(wr => wr.RepositoryId);

        var rows = new List<BulkMergePrRow>();
        foreach (var repositoryId in ids)
        {
            if (!persistedPrs.TryGetValue(repositoryId, out var pr) || pr == null)
                continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!linkByRepoId.TryGetValue(repositoryId, out var link) || link.Repository == null)
                continue;

            rows.Add(new BulkMergePrRow
            {
                RepositoryId = repositoryId,
                RepositoryName = link.Repository.RepositoryName,
                PrNumber = pr.Number,
                PrHtmlUrl = string.IsNullOrWhiteSpace(pr.HtmlUrl) ? null : pr.HtmlUrl,
                Mergeable = pr.Mergeable,
                MergeableState = pr.MergeableState,
                IsSelected = pr.Mergeable != false,
                Status = BulkMergeRowStatus.LoadingSnapshot
            });
        }

        if (rows.Count == 0)
        {
            ToastService.Show("No open pull requests in this level.");
            return;
        }

        rows = rows.OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase).ToList();

        _bulkMergeModal = new BulkMergePullRequestsModalState
        {
            IsVisible = true,
            LevelKey = levelKey,
            LevelLabel = levelKey.HasValue ? $"Level {levelKey}" : "No dependencies",
            Rows = rows
        };
        StateHasChanged();

        _ = LoadBulkMergeSnapshotsAsync(_bulkMergeModal, rows);
    }

    /// <summary>
    /// Cheap per-row snapshot fetch (title/branches/mergeability) - the same fast phase OpenMergeDialogAsync
    /// already uses for one PR, just looped with bounded concurrency. Also pulls the row's local git state
    /// (uncommitted changes / unpushed / incoming commits) - a plain read of the already-persisted Git Changes
    /// projection, no extra GitHub call - so a row never shows green while the local clone is out of sync.
    /// </summary>
    private async Task LoadBulkMergeSnapshotsAsync(BulkMergePullRequestsModalState modalGeneration, List<BulkMergePrRow> rows)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));
        var tasks = rows.Select(async row =>
        {
            await semaphore.WaitAsync();
            try
            {
                PullRequestMergeSnapshot? snapshot = null;
                try
                {
                    snapshot = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeSnapshot?>(
                        svc => svc.GetMergeSnapshotAsync(WorkspaceId, row.RepositoryId, row.PrNumber));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to load bulk-merge snapshot for repo {RepositoryId}, PR #{PrNumber}", row.RepositoryId, row.PrNumber);
                }

                LocalGitState localState = default;
                try
                {
                    localState = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, LocalGitState>(
                        svc => svc.GetLocalGitStateAsync(WorkspaceId, row.RepositoryId));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to load local git state for repo {RepositoryId}", row.RepositoryId);
                }

                SafeInvoke(() =>
                {
                    if (_bulkMergeModal != modalGeneration)
                        return;

                    if (snapshot != null)
                    {
                        row.Title = snapshot.Title;
                        row.HeadSha = snapshot.HeadSha;
                        if (!string.IsNullOrWhiteSpace(snapshot.HtmlUrl))
                            row.PrHtmlUrl = snapshot.HtmlUrl;
                        row.Mergeable = snapshot.Mergeable;
                        row.MergeableState = snapshot.MergeableState;
                        row.IsSelected = snapshot.Mergeable != false;
                    }

                    row.HasLocalWarning = localState.HasLocalWarning;
                    row.UncommittedChangesCount = localState.UncommittedChangesCount;
                    row.UnpushedCommitsCount = localState.UnpushedCommitsCount;
                    row.IncomingCommitsCount = localState.IncomingCommitsCount;

                    row.Status = ResolveMergeabilityStatus(row.Mergeable);
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Re-fetches the merge snapshot for every row still waiting on GitHub's mergeability computation
    /// (<see cref="BulkMergeRowStatus.CheckingMergeability"/> - Mergeable null / MergeableState "unknown").
    /// GitHub can take a few seconds after a PR is opened/updated to finish that check, so this is called from
    /// the same background PR polling loop that already keeps the grid and the single-PR merge dialog fresh
    /// (WorkspaceRepositories.PrPolling.cs). Only runs while the dialog is still in its selection phase - once
    /// a merge (or retry) is in flight or has finished, row status is driven by the merge/sync steps instead.
    /// </summary>
    private async Task RefreshBulkMergeMergeabilityIfDueAsync()
    {
        if (_disposed || !_bulkMergeModal.IsVisible || _bulkMergeModal.IsRunning || _bulkMergeModal.HasRun)
            return;

        var modalGeneration = _bulkMergeModal;
        var pendingRows = modalGeneration.Rows.Where(r => r.Status == BulkMergeRowStatus.CheckingMergeability).ToList();
        if (pendingRows.Count == 0)
            return;

        using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));
        var tasks = pendingRows.Select(async row =>
        {
            await semaphore.WaitAsync();
            try
            {
                PullRequestMergeSnapshot? snapshot;
                try
                {
                    snapshot = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeSnapshot?>(
                        svc => svc.GetMergeSnapshotAsync(WorkspaceId, row.RepositoryId, row.PrNumber));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to refresh bulk-merge mergeability for repo {RepositoryId}, PR #{PrNumber}", row.RepositoryId, row.PrNumber);
                    return;
                }

                if (snapshot == null)
                    return;

                SafeInvoke(() =>
                {
                    if (_bulkMergeModal != modalGeneration || row.Status != BulkMergeRowStatus.CheckingMergeability)
                        return;

                    row.Mergeable = snapshot.Mergeable;
                    row.MergeableState = snapshot.MergeableState;
                    row.IsSelected = snapshot.Mergeable != false;
                    row.Status = ResolveMergeabilityStatus(row.Mergeable);
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>Never reports null Mergeable (GitHub still computing) as Ready or as a failure - see <see cref="BulkMergeRowStatus.CheckingMergeability"/>.</summary>
    private static BulkMergeRowStatus ResolveMergeabilityStatus(bool? mergeable) => mergeable switch
    {
        false => BulkMergeRowStatus.Conflict,
        null => BulkMergeRowStatus.CheckingMergeability,
        _ => BulkMergeRowStatus.Ready
    };

    private void SetBulkMergeMethod(BulkMergeMethodSelection method)
    {
        _bulkMergeModal.SelectedMethod = method;
        StateHasChanged();
    }

    private void SetBulkMergeReturnToDefault(bool value)
    {
        BulkMergeReturnToDefault = value;
        StateHasChanged();
    }

    private void CloseBulkMergeModal()
    {
        if (_bulkMergeModal.IsRunning)
            return;
        _bulkMergeModal.IsVisible = false;
        StateHasChanged();
    }

    /// <summary>Confirms before firing the batch, since selecting many rows and clicking once is easy to do by accident and there is no per-PR undo once GitHub accepts the merge.</summary>
    private void ExecuteBulkMerge()
    {
        var rows = _bulkMergeModal.Rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            return;

        // Captured once here (the "Return to default branch" checkbox is only editable during this pre-run
        // selection phase) rather than re-read from the shared preference singleton on every later retry -
        // otherwise a "Retry failed" click could pick up whatever that singleton now holds (e.g. changed via
        // the single-PR merge dialog in the meantime) instead of the choice this batch was actually confirmed
        // with, leaving sibling rows from the same run inconsistently synced.
        // Feature contexts never return to default after merge.
        var returnToDefault = !_isFeatureContext && BulkMergeReturnToDefault;
        var methodPhrase = _bulkMergeModal.SelectedMethod.ToConfirmationPhrase();
        var syncSuffix = returnToDefault ? " and synced to the default branch" : string.Empty;
        var message = $"Merge {rows.Count} pull request{(rows.Count == 1 ? "" : "s")} using {methodPhrase}{syncSuffix}?";

        ShowConfirm(message, () =>
        {
            _bulkMergeModal.ReturnToDefault = returnToDefault;
            RunBulkMergeAndReturnToDefaultAsync(rows, Array.Empty<BulkMergePrRow>());
            return Task.CompletedTask;
        }, "Merge");
    }

    /// <summary>Retries every currently-failed row: Failed rows re-merge (and, if the checkbox is on, return to default); ReturnFailed rows only re-run the return-to-default step, since their merge already succeeded and re-issuing it would just have GitHub reject an already-merged PR.</summary>
    private void RetryFailedBulkMerge()
    {
        var failedRows = _bulkMergeModal.Rows.Where(r => r.Status == BulkMergeRowStatus.Failed).ToList();
        var returnFailedRows = _bulkMergeModal.Rows.Where(r => r.Status == BulkMergeRowStatus.ReturnFailed).ToList();
        RunBulkMergeAndReturnToDefaultAsync(failedRows, returnFailedRows);
    }

    /// <summary>Same Failed-vs-ReturnFailed split as <see cref="RetryFailedBulkMerge"/>, for a single row's retry icon.</summary>
    private void RetryBulkMergeRow(BulkMergePrRow row) =>
        RunBulkMergeAndReturnToDefaultAsync(
            row.Status == BulkMergeRowStatus.ReturnFailed ? Array.Empty<BulkMergePrRow>() : [row],
            row.Status == BulkMergeRowStatus.ReturnFailed ? [row] : Array.Empty<BulkMergePrRow>());

    /// <summary>
    /// Merges <paramref name="rowsToMerge"/> via the plural IWorkspacePullRequestOperations.MergeManyAsync, then
    /// syncs to default (one repo at a time) whichever of those succeed - plus, unconditionally,
    /// <paramref name="rowsToReturnOnly"/> (rows whose merge already succeeded on an earlier run and only need
    /// their sync step retried, regardless of the live "return to default" checkbox state). Runs as the standard
    /// page job so BackgroundJobOverlay's LoadingOverlay (with terminal) covers the still-mounted dialog.
    /// Reused for the primary "Merge N pull requests" button, "Retry failed (n)", and a single row's retry icon.
    /// </summary>
    private void RunBulkMergeAndReturnToDefaultAsync(IReadOnlyList<BulkMergePrRow> rowsToMerge, IReadOnlyList<BulkMergePrRow> rowsToReturnOnly)
    {
        var touchedRows = rowsToMerge.Concat(rowsToReturnOnly).ToList();
        if (touchedRows.Count == 0 || IsJobRunning || !_bulkMergeModal.IsVisible)
            return;

        foreach (var row in rowsToMerge)
        {
            row.Status = BulkMergeRowStatus.Merging;
            row.ErrorMessage = null;
        }

        _bulkMergeModal.IsRunning = true;
        _bulkMergeModal.HasRun = false;
        _bulkMergeModal.Completed = 0;
        _bulkMergeModal.Failed = 0;
        StateHasChanged();

        var returnToDefault = _bulkMergeModal.ReturnToDefault;
        var method = ToMergeMethod(_bulkMergeModal.SelectedMethod);
        var requests = rowsToMerge.Select(r => new MergePullRequestRequest
        {
            RepositoryId = r.RepositoryId,
            PrNumber = r.PrNumber,
            Method = method,
            ExpectedHeadSha = r.HeadSha
        }).ToList();
        var rowByRepoId = rowsToMerge.ToDictionary(r => r.RepositoryId);
        var mergeTotal = requests.Count;

        var label = mergeTotal > 0
            ? (mergeTotal == 1 ? "Merging 1 pull request..." : $"Merging {mergeTotal} pull requests...")
            : (rowsToReturnOnly.Count == 1 ? "Returning 1 repository to default..." : $"Returning {rowsToReturnOnly.Count} repositories to default...");

        StartPageJob(label, async (job, ct) =>
        {
            try
            {
                var succeededFromMerge = new List<BulkMergePrRow>();
                if (mergeTotal > 0)
                {
                    var progress = new Progress<MergePullRequestProgress>(p =>
                    {
                        job.ReportProgress(p.Total == 1 ? "Merging 1 pull request..." : $"Merging {p.Completed} of {p.Total} pull requests...");
                        SafeInvoke(() =>
                        {
                            _bulkMergeModal.Completed = p.Completed;
                            _bulkMergeModal.Failed = p.Failed;
                            if (p.CurrentRepositoryId is int repoId && rowByRepoId.TryGetValue(repoId, out var row))
                            {
                                row.Status = p.CurrentSuccess == true ? BulkMergeRowStatus.Merged : BulkMergeRowStatus.Failed;
                                row.ErrorMessage = p.CurrentSuccess == true ? null : p.CurrentErrorMessage;
                                if (p.CurrentSuccess == true)
                                    ClearRepositoryError(repoId);
                            }
                        });
                    });

                    // Not re-thrown: rowsToReturnOnly below is an independent step (previously-merged rows only
                    // needing their sync retried) and must still run even if this merge phase blew up entirely,
                    // so a batch that mixes "needs re-merge" and "needs sync only" rows (RetryFailedBulkMerge)
                    // never silently drops the sync-only half. The affected rows are already marked Failed with
                    // the error surfaced per-row, matching how an individual GitHub merge failure (which never
                    // throws - MergePullRequestResult.Success is just false) is already reported, so the job
                    // itself does not need to fault too.
                    IReadOnlyList<MergePullRequestResult> results = Array.Empty<MergePullRequestResult>();
                    try
                    {
                        results = await PullRequestOperations.MergeManyAsync(WorkspaceId, requests, progress, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.LogError(ex, "Bulk merge failed for workspace {WorkspaceId}", WorkspaceId);
                        SafeInvoke(() =>
                        {
                            foreach (var row in rowsToMerge.Where(r => r.Status == BulkMergeRowStatus.Merging))
                            {
                                row.Status = BulkMergeRowStatus.Failed;
                                row.ErrorMessage = ex.Message;
                            }
                        });
                    }

                    succeededFromMerge = results
                        .Where(r => r.Success)
                        .Select(r => rowByRepoId.TryGetValue(r.RepositoryId, out var row) ? row : null)
                        .Where(row => row != null)
                        .Select(row => row!)
                        .ToList();
                }

                if (rowsToReturnOnly.Count > 0)
                    await RunBulkReturnToDefaultAsync(rowsToReturnOnly, job, ct);

                if (returnToDefault && succeededFromMerge.Count > 0)
                    await RunBulkReturnToDefaultAsync(succeededFromMerge, job, ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            finally
            {
                // Always runs - on success, fault, and Abort-triggered cancellation alike - so the dialog can never
                // get stuck with close disabled if a later phase (the return-to-default step) is what actually threw/was cancelled
                // rather than the merge call itself.
                SafeInvoke(() => FinishBulkMergeRun(touchedRows));
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            CancelToast = "Merge cancelled.",
            OnError = ex => Logger.LogError(ex, "Bulk merge job failed for workspace {WorkspaceId}", WorkspaceId)
        });
    }

    /// <summary>
    /// Calls ReturnToDefaultAsync once per successfully-merged row, never once for the whole set -
    /// ReturnToDefaultAsync aborts its entire call on first failure with no per-repo attribution.
    /// Sequential on purpose: each unattended sync recomputes workspace-wide stats at the end, and
    /// running those in parallel races SQLite plus the recompute (the same race ReturnToDefaultLevelAsync
    /// documents and avoids). Overlay progress goes through the standard page-job terminal.
    /// </summary>
    private async Task RunBulkReturnToDefaultAsync(IReadOnlyList<BulkMergePrRow> succeededRows, BackgroundJobHandle job, CancellationToken ct)
    {
        SafeInvoke(() =>
        {
            foreach (var row in succeededRows)
                row.Status = BulkMergeRowStatus.ReturningToDefault;
        });

        var progress = job.ToOperationProgress();
        foreach (var row in succeededRows)
        {
            ct.ThrowIfCancellationRequested();

            UnattendedReturnToDefaultResult syncResult;
            try
            {
                syncResult = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, UnattendedReturnToDefaultResult>(
                    svc => svc.ReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), [row.RepositoryId], progress, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                syncResult = new UnattendedReturnToDefaultResult(false, ex.Message);
            }

            SafeInvoke(() =>
            {
                if (syncResult.Completed)
                {
                    row.Status = BulkMergeRowStatus.ReturnedToDefault;
                    ClearRepositoryError(row.RepositoryId);
                }
                else
                {
                    row.Status = BulkMergeRowStatus.ReturnFailed;
                    row.ErrorMessage = syncResult.AbortReason;
                    SetRepositoryError(row.RepositoryId, syncResult.AbortReason ?? "Failed to return to default branch.");
                }
            });
        }
    }

    /// <summary>
    /// Marks any row this run left in a non-terminal state as Skipped (still Merging - unattempted when Abort was
    /// pressed) or ReturnFailed (still ReturningToDefault - the merge itself already succeeded, only the return-to-default step was
    /// interrupted), and flips the modal from running to its finished summary. Runs unconditionally from the job's
    /// own finally, so it fires whether the run finished, faulted, or was cancelled via the overlay Abort.
    /// </summary>
    private void FinishBulkMergeRun(IReadOnlyList<BulkMergePrRow> touchedRows)
    {
        foreach (var row in touchedRows)
        {
            if (row.Status == BulkMergeRowStatus.Merging)
            {
                row.Status = BulkMergeRowStatus.Skipped;
            }
            else if (row.Status == BulkMergeRowStatus.ReturningToDefault)
            {
                row.Status = BulkMergeRowStatus.ReturnFailed;
                row.ErrorMessage = "Return to default branch was cancelled.";
            }
        }

        _bulkMergeModal.IsRunning = false;
        _bulkMergeModal.HasRun = true;
    }

    private static MergeMethod? ToMergeMethod(BulkMergeMethodSelection selection) => selection switch
    {
        BulkMergeMethodSelection.Squash => MergeMethod.Squash,
        BulkMergeMethodSelection.Merge => MergeMethod.Merge,
        BulkMergeMethodSelection.Rebase => MergeMethod.Rebase,
        _ => null
    };

    /// <summary>Info icon drill-in: opens the existing single-PR MergePullRequestModal, unchanged, stacked on top for this one repo/PR. Disabled while the batch is running (guarded by the modal itself).</summary>
    private async Task OnBulkMergeRowInfoAsync(BulkMergePrRow row)
    {
        if (_bulkMergeModal.IsRunning)
            return;

        var link = await TryGetLinkAsync(row.RepositoryId);
        if (link == null)
        {
            ToastService.ShowError("Could not load this repository's details.");
            return;
        }

        _bulkMergeModal.DrillInRepositoryId = row.RepositoryId;
        await OpenMergeDialogAsync(link);

        // OpenMergeDialogAsync silently no-ops (no PR number resolved, e.g. an unhydrated row's PR info isn't
        // loaded yet) without ever setting _mergePrModal.IsVisible - leaving DrillInRepositoryId set with no
        // dialog open would otherwise make this row's bulk modal drop under the overlay for any later,
        // unrelated page job (see IsCoveredByOverlay's binding in WorkspaceRepositories.razor).
        if (!_mergePrModal.IsVisible && _bulkMergeModal.DrillInRepositoryId == row.RepositoryId)
            _bulkMergeModal.DrillInRepositoryId = null;
    }

    /// <summary>Called from CloseMergeModal when the single-PR dialog it is closing was opened via drill-in from a bulk row, so that row's snapshot stays consistent with whatever the single-PR dialog may have changed (title edit, close-without-merge, etc).</summary>
    private async Task RefreshBulkMergeRowAfterDrillInAsync(int repositoryId)
    {
        if (!_bulkMergeModal.IsVisible)
            return;

        var row = _bulkMergeModal.Rows.FirstOrDefault(r => r.RepositoryId == repositoryId);
        if (row == null || row.Status is BulkMergeRowStatus.Merging or BulkMergeRowStatus.ReturningToDefault)
            return;

        PullRequestMergeSnapshot? snapshot = null;
        try
        {
            snapshot = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeSnapshot?>(
                svc => svc.GetMergeSnapshotAsync(WorkspaceId, row.RepositoryId, row.PrNumber));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to refresh bulk-merge row after drill-in for repo {RepositoryId}", repositoryId);
            return;
        }

        if (snapshot == null || _disposed || !_bulkMergeModal.IsVisible)
            return;

        LocalGitState localState = default;
        try
        {
            localState = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, LocalGitState>(
                svc => svc.GetLocalGitStateAsync(WorkspaceId, row.RepositoryId));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to refresh local git state after drill-in for repo {RepositoryId}", repositoryId);
        }

        row.Title = snapshot.Title;
        row.HeadSha = snapshot.HeadSha;
        row.Mergeable = snapshot.Mergeable;
        row.MergeableState = snapshot.MergeableState;
        row.HasLocalWarning = localState.HasLocalWarning;
        row.UncommittedChangesCount = localState.UncommittedChangesCount;
        row.UnpushedCommitsCount = localState.UnpushedCommitsCount;
        row.IncomingCommitsCount = localState.IncomingCommitsCount;
        if (row.Status is BulkMergeRowStatus.LoadingSnapshot or BulkMergeRowStatus.CheckingMergeability or BulkMergeRowStatus.Ready or BulkMergeRowStatus.Conflict)
            row.Status = ResolveMergeabilityStatus(row.Mergeable);
        StateHasChanged();
    }

    /// <summary>
    /// Called by the single-PR dialog's own merge/close-without-merging success paths when that dialog was opened
    /// via drill-in from a bulk row - those paths reset the single-PR dialog's state directly rather than through
    /// CloseMergeModal, so the DrillInRepositoryId-based live refetch in <see cref="RefreshBulkMergeRowAfterDrillInAsync"/>
    /// never runs for them. Sets the row to the already-known outcome instead of re-fetching from GitHub, since a
    /// freshly merged/closed PR's own mergeability fields no longer describe an open, mergeable PR (e.g. GitHub
    /// reports Mergeable as null post-merge, which <see cref="ResolveMergeabilityStatus"/> would otherwise read as
    /// "still checking"). Unselects the row so a later "Merge N pull requests" click in the same dialog session
    /// can't re-attempt an action GitHub already completed.
    /// </summary>
    private void ReflectBulkMergeDrillInResult(int repositoryId, BulkMergeRowStatus status, string? errorMessage = null)
    {
        if (!_bulkMergeModal.IsVisible)
            return;

        var row = _bulkMergeModal.Rows.FirstOrDefault(r => r.RepositoryId == repositoryId);
        if (row == null)
            return;

        row.Status = status;
        row.ErrorMessage = errorMessage;
        row.IsSelected = false;
        StateHasChanged();
    }

    /// <summary>
    /// Mutable class (not a record) per CLAUDE.md's exception for modal state that mutates mid-display - dozens
    /// of rows independently updated by background work. Rows are only ever mutated inside SafeInvoke/InvokeAsync
    /// callbacks, never directly from a worker task.
    /// </summary>
    private sealed class BulkMergePullRequestsModalState
    {
        public bool IsVisible { get; set; }
        public int? LevelKey { get; set; }
        public string LevelLabel { get; set; } = string.Empty;
        public List<BulkMergePrRow> Rows { get; set; } = new();
        public BulkMergeMethodSelection SelectedMethod { get; set; } = BulkMergeMethodSelection.Squash;
        /// <summary>Return-to-default choice frozen at the first "Merge N pull requests" confirmation, so later "Retry failed"/single-row retries within this same dialog session use the choice the batch was actually confirmed with, not whatever the shared preference singleton (<see cref="MergePullRequestReturnToDefaultPreferenceService"/>) currently holds.</summary>
        public bool ReturnToDefault { get; set; }
        public bool IsRunning { get; set; }
        public bool HasRun { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        /// <summary>Repository id of the bulk row whose info icon opened the single-PR dialog on top of this one, so closing that dialog can refresh just that one row. Null when the single-PR dialog was opened some other way.</summary>
        public int? DrillInRepositoryId { get; set; }
    }
}
