using GrayMoon.App.Components.Modals;
using GrayMoon.App.Models;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Bulk-merge dialog opened per dependency level from the level-header "..." menu's "Merge PRs..." item, or
/// over every repository from the header menu when the grid is a flat list without level headers.
/// Candidates are resolved authoritatively (GetRepositoryIdsAtLevelAsync + GetAllLinksForOperationAsync joined
/// against the persisted PR table), never from the render-cache <c>prByRepositoryId</c> that
/// GetHydratedLinksAtLevel/HasMergeablePr use - virtual scrolling means most rows are not hydrated.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    [Inject] private MergePullRequestReturnToDefaultPreferenceService ReturnToDefaultPreferenceService { get; set; } = default!;

    private BulkMergePullRequestsModalState _bulkMergeModal = new();
    private string? _bulkReturnReconciliationError;

    /// <summary>Bound to the same singleton the single-PR dialog's own "Return to default branch" checkbox uses, so the two remember one shared last choice.</summary>
    private bool BulkMergeReturnToDefault
    {
        get => ReturnToDefaultPreferenceService.ReturnToDefault;
        set => ReturnToDefaultPreferenceService.ReturnToDefault = value;
    }

    /// <summary>Bulk-merge only: Merged (default) vs All repositories in the dialog scope.</summary>
    private BulkMergeReturnToDefaultScope BulkMergeReturnToDefaultScope
    {
        get => ReturnToDefaultPreferenceService.ReturnToDefaultScope;
        set => ReturnToDefaultPreferenceService.ReturnToDefaultScope = value;
    }

    private async Task OpenMergePullRequestsDialogForLevelAsync(int? levelKey)
    {
        var ids = (await GetRepositoryIdsAtLevelAsync(levelKey)).ToHashSet();
        await OpenMergePullRequestsDialogAsync(
            ids,
            levelKey,
            levelKey.HasValue ? $"Level {levelKey}" : "No dependencies",
            "No open pull requests in this level.");
    }

    /// <summary>Header-menu "Merge PRs..." over an explicit repository id set (Workspace / Repositories headers of a flat grid).</summary>
    private Task OpenMergePullRequestsDialogForIdsAsync(IReadOnlyList<int> repositoryIds, string scopeLabel) =>
        OpenMergePullRequestsDialogAsync(repositoryIds.ToHashSet(), null, scopeLabel, "No open pull requests.");

    /// <summary>Header-menu entry for a flat grid (no level headers): the same dialog over every repository.</summary>
    private async Task OpenMergePullRequestsDialogForAllAsync()
    {
        var ids = (await GetAllLinksForOperationAsync()).Select(wr => wr.RepositoryId).ToHashSet();
        await OpenMergePullRequestsDialogAsync(ids, null, "All repositories", "No open pull requests.");
    }

    private async Task OpenMergePullRequestsDialogAsync(
        IReadOnlySet<int> ids,
        int? levelKey,
        string scopeLabel,
        string nothingToMergeMessage)
    {
        if (ids.Count == 0)
        {
            ToastService.Show(nothingToMergeMessage);
            return;
        }

        IReadOnlyList<WorkspaceRepositoryLink> links;
        IReadOnlyDictionary<int, PullRequestInfo?> persistedPrs;
        try
        {
            links = await GetAllLinksForOperationAsync();
            if (_isFeatureContext && _selectedContextId is { } featureContextId)
            {
                persistedPrs = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, IReadOnlyDictionary<int, PullRequestInfo?>>(
                    svc => svc.GetPersistedPullRequestsForWorkspaceContextAsync(WorkspaceId, featureContextId.Value));
            }
            else
            {
                persistedPrs = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, IReadOnlyDictionary<int, PullRequestInfo?>>(
                    svc => svc.GetPersistedPullRequestsForWorkspaceAsync(WorkspaceId));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load pull request candidates for bulk merge, workspace {WorkspaceId}, level {LevelKey}", WorkspaceId, levelKey);
            ToastService.ShowError("Could not load pull requests for this level.");
            return;
        }

        var linkByRepoId = links.Where(wr => ids.Contains(wr.RepositoryId)).ToDictionary(wr => wr.RepositoryId);

        var rows = new List<BulkMergePrRow>();
        var openPrRepositoryIds = new HashSet<int>();
        foreach (var repositoryId in ids)
        {
            if (!persistedPrs.TryGetValue(repositoryId, out var pr) || pr == null)
                continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!linkByRepoId.TryGetValue(repositoryId, out var link) || link.Repository == null)
                continue;

            openPrRepositoryIds.Add(repositoryId);
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
            ToastService.Show(nothingToMergeMessage);
            return;
        }

        // Repositories in this merge scope with no open PR (usually unchanged on the branch). Used when
        // "Return to default branch" → All repositories is selected so they are returned alongside merges.
        // Tag checkouts are never part of Return to Default (or other mutating ops) - leave them out.
        var noOpenPrRepositories = new List<BulkMergeNoOpenPrRepository>();
        foreach (var repositoryId in ids)
        {
            if (openPrRepositoryIds.Contains(repositoryId))
                continue;
            if (!linkByRepoId.TryGetValue(repositoryId, out var link) || link.Repository == null)
                continue;
            if (link.IsOnTag)
                continue;
            noOpenPrRepositories.Add(new BulkMergeNoOpenPrRepository(repositoryId, link.Repository.RepositoryName));
        }

        rows = rows.OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase).ToList();

        _bulkMergeModal = new BulkMergePullRequestsModalState
        {
            IsVisible = true,
            LevelKey = levelKey,
            LevelLabel = scopeLabel,
            Rows = rows,
            NoOpenPrRepositories = noOpenPrRepositories
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

    private void SetBulkMergeReturnToDefaultScope(BulkMergeReturnToDefaultScope scope)
    {
        BulkMergeReturnToDefaultScope = scope;
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

        // Captured once here (the "Return to default branch" controls are only editable during this pre-run
        // selection phase) rather than re-read from the shared preference singleton on every later retry -
        // otherwise a "Retry failed" click could pick up whatever that singleton now holds (e.g. changed via
        // the single-PR merge dialog in the meantime) instead of the choice this batch was actually confirmed
        // with, leaving sibling rows from the same run inconsistently synced.
        // Feature contexts never return to default after merge.
        var returnToDefault = !_isFeatureContext && BulkMergeReturnToDefault;
        var returnScope = BulkMergeReturnToDefaultScope;
        var methodPhrase = _bulkMergeModal.SelectedMethod.ToConfirmationPhrase();
        var syncSuffix = !returnToDefault
            ? string.Empty
            : returnScope == BulkMergeReturnToDefaultScope.AllRepositories
                ? " and return all repositories to the default branch"
                : " and synced to the default branch";
        var message = $"Merge {rows.Count} pull request{(rows.Count == 1 ? "" : "s")} using {methodPhrase}{syncSuffix}?";

        ShowConfirm(message, () =>
        {
            _bulkMergeModal.ReturnToDefault = returnToDefault;
            _bulkMergeModal.ReturnToDefaultScope = returnScope;
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
    /// returns to default (one bounded batch) whichever of those succeed - plus, unconditionally,
    /// <paramref name="rowsToReturnOnly"/> (rows whose merge already succeeded on an earlier run and only need
    /// their return step retried, regardless of the live "return to default" checkbox state). Hides the dialog for
    /// the run so BackgroundJobOverlay's LoadingOverlay (with terminal) is the only UI; on full success a toast
    /// closes the flow, and on any failure the dialog reopens as the completed-run summary.
    /// Reused for the primary "Merge N pull requests" button, "Retry failed (n)", and a single row's retry icon.
    /// </summary>
    private void RunBulkMergeAndReturnToDefaultAsync(IReadOnlyList<BulkMergePrRow> rowsToMerge, IReadOnlyList<BulkMergePrRow> rowsToReturnOnly)
    {
        var touchedRows = rowsToMerge.Concat(rowsToReturnOnly).ToList();
        if (touchedRows.Count == 0 || IsJobRunning || !_bulkMergeModal.IsVisible)
            return;

        _bulkReturnReconciliationError = null;

        foreach (var row in rowsToMerge)
        {
            row.Status = BulkMergeRowStatus.Merging;
            row.ErrorMessage = null;
        }

        _bulkMergeModal.IsRunning = true;
        _bulkMergeModal.HasRun = false;
        _bulkMergeModal.Completed = 0;
        _bulkMergeModal.Failed = 0;
        _bulkMergeModal.IsVisible = false;
        StateHasChanged();

        var returnToDefault = _bulkMergeModal.ReturnToDefault;
        var returnScope = _bulkMergeModal.ReturnToDefaultScope;
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

                var includeNoOpenPrReturns = returnToDefault &&
                    returnScope == BulkMergeReturnToDefaultScope.AllRepositories &&
                    _bulkMergeModal.NoOpenPrRepositories.Count > 0;

                if (rowsToReturnOnly.Count > 0 || (returnToDefault && succeededFromMerge.Count > 0) || includeNoOpenPrReturns)
                {
                    var returnRows = new List<BulkMergePrRow>(
                        rowsToReturnOnly.Count + succeededFromMerge.Count + (includeNoOpenPrReturns ? _bulkMergeModal.NoOpenPrRepositories.Count : 0));
                    var seenRepositoryIds = new HashSet<int>();
                    foreach (var row in rowsToReturnOnly)
                    {
                        if (seenRepositoryIds.Add(row.RepositoryId))
                            returnRows.Add(row);
                    }

                    if (returnToDefault)
                    {
                        foreach (var row in succeededFromMerge)
                        {
                            if (seenRepositoryIds.Add(row.RepositoryId))
                                returnRows.Add(row);
                        }

                        if (includeNoOpenPrReturns)
                        {
                            List<BulkMergePrRow> extras = [];
                            await InvokeAsync(() =>
                            {
                                if (_disposed)
                                    return;
                                extras = EnsureNoOpenPrReturnRows(seenRepositoryIds);
                                if (extras.Count > 0)
                                {
                                    returnRows.AddRange(extras);
                                    touchedRows.AddRange(extras);
                                }
                            });
                        }
                    }

                    if (returnRows.Count > 0)
                        await RunBulkReturnToDefaultAsync(returnRows, job, ct);
                }

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
    /// Ensures each no-open-PR repository in the dialog scope has a companion row for return-to-default tracking
    /// when <see cref="BulkMergeReturnToDefaultScope.AllRepositories"/> is selected. Skips ids already queued
    /// (merged PRs) and repositories already successfully returned. New rows are appended to the modal list so
    /// a failed return can surface in the completed-run summary.
    /// </summary>
    private List<BulkMergePrRow> EnsureNoOpenPrReturnRows(HashSet<int> seenRepositoryIds)
    {
        var extras = new List<BulkMergePrRow>();
        foreach (var repo in _bulkMergeModal.NoOpenPrRepositories)
        {
            if (!seenRepositoryIds.Add(repo.RepositoryId))
                continue;

            var existing = _bulkMergeModal.Rows.FirstOrDefault(r =>
                r.HasNoOpenPullRequest && r.RepositoryId == repo.RepositoryId);
            if (existing != null)
            {
                if (existing.Status == BulkMergeRowStatus.ReturnedToDefault)
                    continue;
                extras.Add(existing);
                continue;
            }

            var row = new BulkMergePrRow
            {
                RepositoryId = repo.RepositoryId,
                RepositoryName = repo.RepositoryName,
                PrNumber = 0,
                HasNoOpenPullRequest = true,
                IsSelected = false,
                Status = BulkMergeRowStatus.ReturningToDefault
            };
            _bulkMergeModal.Rows.Add(row);
            extras.Add(row);
        }

        return extras;
    }

    /// <summary>
    /// Returns every distinct repository in one batch. Each repository keeps its own success or failure.
    /// Workspace recompute runs once after the batch, not once per repository.
    /// </summary>
    private async Task RunBulkReturnToDefaultAsync(IReadOnlyList<BulkMergePrRow> succeededRows, BackgroundJobHandle job, CancellationToken ct)
    {
        await InvokeAsync(() =>
        {
            if (_disposed)
                return;
            foreach (var row in succeededRows)
                row.Status = BulkMergeRowStatus.ReturningToDefault;
            StateHasChanged();
        });

        UnattendedReturnToDefaultBatchResult batch;
        try
        {
            batch = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, UnattendedReturnToDefaultBatchResult>(
                svc => svc.ReturnToDefaultBatchAsync(
                    WorkspaceId,
                    RequireSelectedContextId(),
                    succeededRows.Select(row => row.RepositoryId).ToList(),
                    job.ToOperationProgress(),
                    ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await InvokeAsync(() =>
            {
                if (_disposed)
                    return;
                foreach (var row in succeededRows.Where(row => row.Status == BulkMergeRowStatus.ReturningToDefault))
                {
                    row.Status = BulkMergeRowStatus.ReturnFailed;
                    row.ErrorMessage = ex.Message;
                    SetRepositoryError(row.RepositoryId, ex.Message);
                }
                StateHasChanged();
            });
            return;
        }

        await InvokeAsync(() =>
        {
            if (_disposed)
                return;
            BulkMergeReturnStatus.Apply(succeededRows, batch.Results, ClearRepositoryError, SetRepositoryError);
            _bulkReturnReconciliationError = batch.ReconciliationSucceeded ? null : batch.ReconciliationError;
            StateHasChanged();
        });

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Marks any row this run left in a non-terminal state as Skipped (still Merging - unattempted when Abort was
    /// pressed) or ReturnFailed (still ReturningToDefault - the merge itself already succeeded, only the return-to-default step was
    /// interrupted), and flips the modal from running to its finished summary. Full success keeps the dialog hidden
    /// and toasts; any failure (or cancel mid-run) brings the dialog back with results. Runs unconditionally from the
    /// job's own finally, so it fires whether the run finished, faulted, or was cancelled via the overlay Abort.
    /// </summary>
    private void FinishBulkMergeRun(IReadOnlyList<BulkMergePrRow> touchedRows)
    {
        BulkMergeRunFinalizer.FinalizeInterruptedRows(touchedRows);

        _bulkMergeModal.IsRunning = false;
        _bulkMergeModal.HasRun = true;

        var counts = BulkMergeRunFinalizer.Count(touchedRows);
        if (counts.Failed == 0 && counts.ReturnFailed == 0 && counts.Skipped == 0)
        {
            _bulkMergeModal.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(_bulkReturnReconciliationError))
            {
                ToastService.ShowError(counts.Succeeded == 1
                    ? "The repository returned to the default branch, but refreshing the workspace failed."
                    : "Repositories returned to the default branch, but refreshing the workspace failed.");
            }
            else
            {
                ToastService.Show(counts.Succeeded == 1
                    ? "1 pull request merged successfully."
                    : $"{counts.Succeeded} pull requests merged successfully.");
            }
            return;
        }

        // Incomplete / failed / cancelled - reopen the completed-run summary. Cancel already toasts via
        // PageJobOptions.CancelToast, so only real merge/return failures get a red toast here.
        _bulkMergeModal.IsVisible = true;

        if (counts.Failed == 0 && counts.RealReturnFailed == 0)
            return;

        if (counts.Failed > 0 && counts.RealReturnFailed > 0)
        {
            ToastService.ShowError("Bulk merge finished with failures.");
        }
        else if (counts.Failed > 0)
        {
            ToastService.ShowError(counts.Failed == 1
                ? "1 pull request failed to merge."
                : $"{counts.Failed} pull requests failed to merge.");
        }
        else
        {
            ToastService.ShowError(counts.RealReturnFailed == 1
                ? "1 pull request merged, but return to default failed."
                : $"{counts.RealReturnFailed} pull requests merged, but return to default failed.");
        }
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
        if (_bulkMergeModal.IsRunning || row.HasNoOpenPullRequest)
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
        /// <summary>Frozen with <see cref="ReturnToDefault"/> at confirm time. <see cref="BulkMergeReturnToDefaultScope.AllRepositories"/> also returns repositories in this dialog's scope that had no open PR.</summary>
        public BulkMergeReturnToDefaultScope ReturnToDefaultScope { get; set; } = BulkMergeReturnToDefaultScope.Merged;
        /// <summary>Repositories in the dialog scope with no open PR when the dialog opened. Used only for All repositories return.</summary>
        public List<BulkMergeNoOpenPrRepository> NoOpenPrRepositories { get; set; } = new();
        public bool IsRunning { get; set; }
        public bool HasRun { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        /// <summary>Repository id of the bulk row whose info icon opened the single-PR dialog on top of this one, so closing that dialog can refresh just that one row. Null when the single-PR dialog was opened some other way.</summary>
        public int? DrillInRepositoryId { get; set; }
    }

    /// <summary>A repository in the bulk-merge scope that had no open PR when the dialog opened.</summary>
    private readonly record struct BulkMergeNoOpenPrRepository(int RepositoryId, string RepositoryName);
}
