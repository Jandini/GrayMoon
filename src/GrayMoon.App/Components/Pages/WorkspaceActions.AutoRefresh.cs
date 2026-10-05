using GrayMoon.Application.Features;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    /// <summary>Wakes the auto-poll loop immediately when the user resumes activity (from Idle or Hidden),
    /// instead of leaving a running-workflow row stuck showing stale status for up to 30s.</summary>
    private void OnActivityBecameActive() => _autoPollWakeCts?.Cancel();

    private static int CurrentAutoPollDelayMs(AppActivityState state) => state switch
    {
        AppActivityState.Active => AutoPollIntervalActiveMs,
        AppActivityState.Idle => AutoPollIntervalIdleMs,
        AppActivityState.Hidden => AutoPollIntervalHiddenMs,
        _ => AutoPollIntervalIdleMs,
    };

    /// <summary>GitHub may lag listing a new run after a push; retry until any workflow line is running with a run id.</summary>
    private async Task<bool> TryRefreshUntilAnyWorkflowRunningAsync(
        WorkspaceActionRow row,
        CancellationToken cancellationToken)
    {
        const string operationLabel = "PushHookGhaVisibility";

        for (var attempt = 1; attempt <= RunWorkflowVisibilityMaxAttempts; attempt++)
        {
            var delayMs = attempt == 1 ? RunWorkflowVisibilityFirstDelayMs : RunWorkflowVisibilityRetryDelayMs;
            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            await RefreshRowAsync(row, cancellationToken);

            if (!string.IsNullOrWhiteSpace(row.ErrorMessage))
            {
                Logger.LogWarning(
                    "{Operation}: refresh failed for {Repo} after attempt {Attempt}",
                    operationLabel,
                    row.Repo.RepositoryName,
                    attempt);
                return false;
            }

            if (row.WorkflowLines.Any(line => IsLineRunningForBranch(row, line) && (line.Action?.RunId ?? 0) > 0))
            {
                Logger.LogInformation(
                    "{Operation}: running workflow visible after attempt {Attempt}/{Max} for {Repo}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    row.Repo.RepositoryName);
                return true;
            }

            if (attempt < RunWorkflowVisibilityMaxAttempts)
            {
                Logger.LogDebug(
                    "{Operation}: attempt {Attempt}/{Max} - no running workflow in API yet for {Repo}",
                    operationLabel,
                    attempt,
                    RunWorkflowVisibilityMaxAttempts,
                    row.Repo.RepositoryName);
            }
        }

        Logger.LogDebug(
            "{Operation}: no running workflow after {Max} attempts for {Repo} (may still be queued)",
            operationLabel,
            RunWorkflowVisibilityMaxAttempts,
            row.Repo.RepositoryName);
        return false;
    }

    private void StartBackgroundRefresh()
    {
        if (workspace == null || rows.Count == 0) return;
        _ = RunFullRefreshAsync(replaceToken: false);
    }

    /// <summary>
    /// Full-grid refresh used by the on-open warm-up and the header Refresh button. Tracks
    /// completed/total so the Git Changes-style header indicator can show progress, and uses
    /// <see cref="_refreshGeneration"/> so a superseded or aborted run cannot clear a newer one.
    /// </summary>
    private async Task RunFullRefreshAsync(bool replaceToken)
    {
        var targets = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName))
            .ToList();
        if (targets.Count == 0) return;

        var generation = Interlocked.Increment(ref _refreshGeneration);
        isRefreshing = true;
        _refreshTotal = targets.Count;
        _refreshCompleted = 0;
        await InvokeAsync(StateHasChanged);

        try
        {
            if (replaceToken)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;
            using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            await Task.WhenAll(targets.Select(row => RefreshScanRowAsync(row, semaphore, token, generation)));
        }
        catch (OperationCanceledException)
        {
            // Abort or a newer full-grid refresh superseded this run.
        }
        finally
        {
            if (!_disposed && generation == _refreshGeneration)
            {
                isRefreshing = false;
                try
                {
                    await InvokeAsync(StateHasChanged);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    private async Task RefreshScanRowAsync(
        WorkspaceActionRow row,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken,
        int generation)
    {
        try
        {
            await RefreshRowThrottledAsync(row, semaphore, cancellationToken);
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                Interlocked.Increment(ref _refreshCompleted);
                if (!_disposed)
                {
                    try
                    {
                        await InvokeAsync(StateHasChanged);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
    }

    /// <summary>Cancels the in-flight full-grid refresh (background warm-up or manual Refresh), matching Git Changes' header abort.</summary>
    internal void AbortRefresh()
    {
        if (!isRefreshing) return;

        Interlocked.Increment(ref _refreshGeneration);
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        isRefreshing = false;
        _refreshTotal = 0;
        _refreshCompleted = 0;
        foreach (var row in rows)
            row.IsRefreshing = false;

        if (rows.Any(r => r.WorkflowLines.Any(line => IsLineRunningForBranch(r, line))))
            EnsureAutoPollRunning();

        StateHasChanged();
    }

    /// <summary>Bounds full-grid refresh fan-out to <see cref="MaxConcurrency"/> concurrent GitHub calls, avoiding secondary rate-limit ("abuse") bursts on large workspaces.</summary>
    private async Task RefreshRowThrottledAsync(WorkspaceActionRow row, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        try
        {
            await semaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await RefreshRowAsync(row, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void EnsureAutoPollRunning()
    {
        if (_autoPollRunning) return;
        _autoPollRunning = true;
        _ = AutoPollLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Keeps polling every <see cref="AutoPollIntervalMs"/> while any row shows "running", so a GHA row's badge
    /// (and its embedded live terminal) reflect GitHub's actual run status until it settles. Each iteration's
    /// work is isolated in its own try/catch: a single unexpected failure (e.g. a transient network blip
    /// surfacing as a stream/TLS error that <see cref="RefreshRowAsync"/> doesn't fully absorb, or a disposed
    /// circuit mid-navigation) must never silently kill this loop - if it did, the row stays stuck showing
    /// "running" indefinitely because nothing else re-polls it until an unrelated event (workspace sync, manual
    /// refresh) happens to trigger a fresh <see cref="EnsureAutoPollRunning"/> call. Only cancellation (page
    /// disposed / RefreshAllAsync superseding this loop) is allowed to actually end the loop.
    /// </summary>
    private async Task AutoPollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delayMs = CurrentAutoPollDelayMs(ActivityStateService.State);
                _autoPollWakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    await Task.Delay(delayMs, _autoPollWakeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    // Otherwise this was a wake-up from OnActivityBecameActive - fall through and poll now.
                }
                finally
                {
                    _autoPollWakeCts.Dispose();
                    _autoPollWakeCts = null;
                }

                if (cancellationToken.IsCancellationRequested) break;

                var runningRows = rows
                    .Where(r =>
                        !string.IsNullOrWhiteSpace(r.Link.BranchName) &&
                        r.WorkflowLines.Any(line => IsLineRunningForBranch(r, line)))
                    .ToList();

                if (runningRows.Count == 0) break;

                try
                {
                    // Bounded to MaxConcurrency, same as RefreshRowThrottledAsync, so a workspace with many
                    // repos "running" at once doesn't fan out an unbounded burst of GitHub calls every tick.
                    using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
                    var tasks = runningRows.Select(row => RefreshRowThrottledAsync(row, semaphore, cancellationToken)).ToList();
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Swallow and keep looping - see summary above. RefreshRowAsync already logs its own
                    // per-row failures; this only catches something escaping that isolation (e.g. a disposed
                    // circuit during StateHasChanged), which must not stop future polling attempts.
                    Logger.LogWarning(ex, "GHA auto-poll iteration failed unexpectedly for workspace {WorkspaceId}; continuing to poll", WorkspaceId);
                }
            }
        }
        catch (OperationCanceledException) { /* page disposed */ }
        finally
        {
            _autoPollRunning = false;
        }
    }

    private async Task RefreshRowAsync(WorkspaceActionRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Link.BranchName)) return;

        try
        {
            row.IsRefreshing = true;

            // Uses its own DI scope (not the circuit-scoped ActionService field) because this method is
            // invoked from fire-and-forget background refresh loops (StartBackgroundRefresh, AutoPollLoopAsync,
            // visibility-retry loops) that can still be mid-flight when the page is disposed (e.g. browser
            // refresh tears down the circuit). Using the injected ActionService there would persist through an
            // AppDbContext already disposed with the old circuit.
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var actionService = scope.ServiceProvider.GetRequiredService<WorkspaceActionService>();

            var list = _isFeatureContext && _selectedContextId is WorkspaceFeatureContextId ctxForFetch
                ? await actionService.FetchAndPersistContextAsync(
                    ctxForFetch.Value,
                    row.Link.WorkspaceRepositoryId,
                    row.Repo,
                    row.Link.BranchName!,
                    cancellationToken)
                : await actionService.FetchAndPersistAsync(
                    row.Link.WorkspaceRepositoryId,
                    row.Repo,
                    row.Link.BranchName!,
                    cancellationToken);

            if (!cancellationToken.IsCancellationRequested && list != null)
            {
                row.WorkflowLines = list.Count > 0
                    ? list.Select(w => new WorkflowActionLine { Action = w }).ToList()
                    : [new WorkflowActionLine()];
                ApplyActionLatches(row);
                row.IsVerified = true;
                row.ErrorMessage = null;

                if (row.WorkflowLines.Any(line => IsLineRunningForBranch(row, line)))
                    EnsureAutoPollRunning();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: either this call's own token was cancelled, or this call coalesced onto an
            // in-flight fetch (WorkspaceActionService.FetchAndPersistAsync) that got cancelled by a
            // newer refresh superseding it (e.g. clicking Refresh while a background poll is in flight).
            // Not a genuine failure, so no error badge should be shown.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing action for {Repo}/{Branch}", row.Repo.RepositoryName, row.Link.BranchName);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                row.IsRefreshing = false;
                try
                {
                    await InvokeAsync(StateHasChanged);
                }
                catch (ObjectDisposedException)
                {
                    // Circuit/renderer torn down between the check above and this call (e.g. browser
                    // navigated away or disconnected past the retention window). Not a real failure -
                    // must not propagate, or it kills the caller's poll loop (AutoPollLoopAsync) and
                    // leaves the row stuck showing its last-known status indefinitely.
                }
                catch (InvalidOperationException)
                {
                    // Same rationale as above - InvokeAsync can throw this once the renderer is gone.
                }
            }
        }
    }

    internal Task RefreshAllAsync()
    {
        if (workspace == null || rows.Count == 0) return Task.CompletedTask;
        return RunFullRefreshAsync(replaceToken: true);
    }
}
