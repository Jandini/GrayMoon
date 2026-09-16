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
    [Inject] private MergePullRequestSyncToDefaultPreferenceService SyncToDefaultPreferenceService { get; set; } = default!;

    private BulkMergePullRequestsModalState _bulkMergeModal = new();

    /// <summary>Bound to the same singleton the single-PR dialog's own "Sync to default branch" checkbox uses, so the two remember one shared last choice.</summary>
    private bool BulkMergeSyncToDefault
    {
        get => SyncToDefaultPreferenceService.SyncToDefault;
        set => SyncToDefaultPreferenceService.SyncToDefault = value;
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

                    row.Status = row.Mergeable == false ? BulkMergeRowStatus.Conflict : BulkMergeRowStatus.Ready;
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private void SetBulkMergeMethod(BulkMergeMethodSelection method)
    {
        _bulkMergeModal.SelectedMethod = method;
        StateHasChanged();
    }

    private void SetBulkMergeSyncToDefault(bool value)
    {
        BulkMergeSyncToDefault = value;
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

        var methodPhrase = _bulkMergeModal.SelectedMethod.ToConfirmationPhrase();
        var syncSuffix = BulkMergeSyncToDefault ? " and synced to the default branch" : string.Empty;
        var message = $"Merge {rows.Count} pull request{(rows.Count == 1 ? "" : "s")} using {methodPhrase}{syncSuffix}?";

        ShowConfirm(message, () =>
        {
            RunBulkMergeAndSyncAsync(rows, Array.Empty<BulkMergePrRow>());
            return Task.CompletedTask;
        }, "Merge");
    }

    /// <summary>Retries every currently-failed row: Failed rows re-merge (and, if the checkbox is on, sync); SyncFailed rows only re-run the sync step, since their merge already succeeded and re-issuing it would just have GitHub reject an already-merged PR.</summary>
    private void RetryFailedBulkMerge()
    {
        var failedRows = _bulkMergeModal.Rows.Where(r => r.Status == BulkMergeRowStatus.Failed).ToList();
        var syncFailedRows = _bulkMergeModal.Rows.Where(r => r.Status == BulkMergeRowStatus.SyncFailed).ToList();
        RunBulkMergeAndSyncAsync(failedRows, syncFailedRows);
    }

    /// <summary>Same Failed-vs-SyncFailed split as <see cref="RetryFailedBulkMerge"/>, for a single row's retry icon.</summary>
    private void RetryBulkMergeRow(BulkMergePrRow row) =>
        RunBulkMergeAndSyncAsync(
            row.Status == BulkMergeRowStatus.SyncFailed ? Array.Empty<BulkMergePrRow>() : [row],
            row.Status == BulkMergeRowStatus.SyncFailed ? [row] : Array.Empty<BulkMergePrRow>());

    /// <summary>
    /// Merges <paramref name="rowsToMerge"/> via the plural IWorkspacePullRequestOperations.MergeManyAsync, then
    /// syncs to default (one repo at a time) whichever of those succeed - plus, unconditionally,
    /// <paramref name="rowsToSyncOnly"/> (rows whose merge already succeeded on an earlier run and only need
    /// their sync step retried, regardless of the live "sync to default" checkbox state). Runs as the standard
    /// page job so BackgroundJobOverlay's LoadingOverlay (with terminal) covers the still-mounted dialog.
    /// Reused for the primary "Merge N pull requests" button, "Retry failed (n)", and a single row's retry icon.
    /// </summary>
    private void RunBulkMergeAndSyncAsync(IReadOnlyList<BulkMergePrRow> rowsToMerge, IReadOnlyList<BulkMergePrRow> rowsToSyncOnly)
    {
        var touchedRows = rowsToMerge.Concat(rowsToSyncOnly).ToList();
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

        var syncToDefault = BulkMergeSyncToDefault;
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
            : (rowsToSyncOnly.Count == 1 ? "Synchronizing 1 repository to default..." : $"Synchronizing {rowsToSyncOnly.Count} repositories to default...");

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

                    IReadOnlyList<MergePullRequestResult> results;
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
                        throw;
                    }

                    succeededFromMerge = results
                        .Where(r => r.Success)
                        .Select(r => rowByRepoId.TryGetValue(r.RepositoryId, out var row) ? row : null)
                        .Where(row => row != null)
                        .Select(row => row!)
                        .ToList();
                }

                if (rowsToSyncOnly.Count > 0)
                    await RunBulkSyncToDefaultAsync(rowsToSyncOnly, job, ct);

                if (syncToDefault && succeededFromMerge.Count > 0)
                    await RunBulkSyncToDefaultAsync(succeededFromMerge, job, ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            finally
            {
                // Always runs - on success, fault, and Abort-triggered cancellation alike - so the dialog can never
                // get stuck with close disabled if a later phase (the sync step) is what actually threw/was cancelled
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
    /// Calls SyncToDefaultAsync once per successfully-merged row, never once for the whole set -
    /// SyncToDefaultAsync aborts its entire call on first failure with no per-repo attribution.
    /// Sequential on purpose: each unattended sync recomputes workspace-wide stats at the end, and
    /// running those in parallel races SQLite plus the recompute (the same race SyncToDefaultLevelAsync
    /// documents and avoids). Overlay progress goes through the standard page-job terminal.
    /// </summary>
    private async Task RunBulkSyncToDefaultAsync(IReadOnlyList<BulkMergePrRow> succeededRows, BackgroundJobHandle job, CancellationToken ct)
    {
        SafeInvoke(() =>
        {
            foreach (var row in succeededRows)
                row.Status = BulkMergeRowStatus.SyncingToDefault;
        });

        var progress = job.ToOperationProgress();
        foreach (var row in succeededRows)
        {
            ct.ThrowIfCancellationRequested();

            UnattendedSyncToDefaultResult syncResult;
            try
            {
                syncResult = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, UnattendedSyncToDefaultResult>(
                    svc => svc.SyncToDefaultAsync(WorkspaceId, [row.RepositoryId], progress, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                syncResult = new UnattendedSyncToDefaultResult(false, ex.Message);
            }

            SafeInvoke(() =>
            {
                if (syncResult.Completed)
                {
                    row.Status = BulkMergeRowStatus.Synced;
                    ClearRepositoryError(row.RepositoryId);
                }
                else
                {
                    row.Status = BulkMergeRowStatus.SyncFailed;
                    row.ErrorMessage = syncResult.AbortReason;
                    SetRepositoryError(row.RepositoryId, syncResult.AbortReason ?? "Failed to sync to default branch.");
                }
            });
        }
    }

    /// <summary>
    /// Marks any row this run left in a non-terminal state as Skipped (still Merging - unattempted when Abort was
    /// pressed) or SyncFailed (still SyncingToDefault - the merge itself already succeeded, only the sync step was
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
            else if (row.Status == BulkMergeRowStatus.SyncingToDefault)
            {
                row.Status = BulkMergeRowStatus.SyncFailed;
                row.ErrorMessage = "Sync to default branch was cancelled.";
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
    }

    /// <summary>Called from CloseMergeModal when the single-PR dialog it is closing was opened via drill-in from a bulk row, so that row's snapshot stays consistent with whatever the single-PR dialog may have changed (title edit, close-without-merge, etc).</summary>
    private async Task RefreshBulkMergeRowAfterDrillInAsync(int repositoryId)
    {
        if (!_bulkMergeModal.IsVisible)
            return;

        var row = _bulkMergeModal.Rows.FirstOrDefault(r => r.RepositoryId == repositoryId);
        if (row == null || row.Status is BulkMergeRowStatus.Merging or BulkMergeRowStatus.SyncingToDefault)
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
        if (row.Status is BulkMergeRowStatus.LoadingSnapshot or BulkMergeRowStatus.Ready or BulkMergeRowStatus.Conflict)
            row.Status = row.Mergeable == false ? BulkMergeRowStatus.Conflict : BulkMergeRowStatus.Ready;
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
        public bool IsRunning { get; set; }
        public bool HasRun { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        /// <summary>Repository id of the bulk row whose info icon opened the single-PR dialog on top of this one, so closing that dialog can refresh just that one row. Null when the single-PR dialog was opened some other way.</summary>
        public int? DrillInRepositoryId { get; set; }
    }
}
