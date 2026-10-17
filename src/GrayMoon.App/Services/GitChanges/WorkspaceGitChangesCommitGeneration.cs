using GrayMoon.App.Services.Ai;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide runs of "write the commit message" started from the provider button on Changes.
/// One run per Workspace and Feature context, so a Feature and the Workspace can be written at the same time.
/// Leaving the page, switching context, or reloading does not cancel a run; only Cancel for that same context does.
/// The written message is stored for that context even when no page is showing it. Not kept across an App restart.
/// The work itself must not use the page's circuit scope (the page starts it through <c>IScopedServiceExecutor</c>).
/// </summary>
public sealed class WorkspaceGitChangesCommitGeneration(
    WorkspaceGitChangesCommitMessageMemory messages,
    ILogger<WorkspaceGitChangesCommitGeneration> logger)
{
    public readonly record struct Key(int WorkspaceId, int ContextId);

    public sealed record Offer(string Message, string ProviderLabel, string? Reason);

    public sealed record Outcome(
        bool Cancelled,
        bool Success,
        string? Message,
        string? Error,
        string ProviderLabel,
        string MessageAtClick)
    {
        public static Outcome CancelledResult { get; } = new(true, false, null, null, "", "");

        public static Outcome Succeeded(string message, string providerLabel, string messageAtClick) =>
            new(false, true, message, null, providerLabel, messageAtClick);

        public static Outcome Failed(string error, string providerLabel, string messageAtClick) =>
            new(false, false, null, error, providerLabel, messageAtClick);
    }

    private sealed class Slot
    {
        public CancellationTokenSource? Cts;
        public string? Error;
        public Offer? Offer;
    }

    private readonly Dictionary<Key, Slot> _slots = new();
    private readonly object _gate = new();

    public event Action<Key>? Changed;

    public bool IsRunning(Key key)
    {
        lock (_gate)
            return _slots.TryGetValue(key, out var slot) && slot.Cts is not null;
    }

    public string? Error(Key key)
    {
        lock (_gate)
            return _slots.TryGetValue(key, out var slot) ? slot.Error : null;
    }

    public Offer? OfferFor(Key key)
    {
        lock (_gate)
            return _slots.TryGetValue(key, out var slot) ? slot.Offer : null;
    }

    /// <summary>
    /// Starts a run for <paramref name="key"/> unless one is already in flight there.
    /// <paramref name="consult"/> is asked when the run succeeds. Return null when that page is no longer showing
    /// <paramref name="key"/> and the message is stored from memory instead. When it returns apply-directly, the page
    /// has already put the message in the box.
    /// </summary>
    public bool TryStart(
        Key key,
        Func<CancellationToken, Task<Outcome>> run,
        Func<Outcome, Task<AiApplyDecision?>>? consult)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var existing) && existing.Cts is not null)
            {
                cts.Dispose();
                return false;
            }

            var slot = existing ?? new Slot();
            slot.Cts = cts;
            slot.Error = null;
            slot.Offer = null;
            _slots[key] = slot;
        }

        Changed?.Invoke(key);
        _ = FinishAsync(key, cts, run, consult);
        return true;
    }

    public void Cancel(Key key)
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _slots.TryGetValue(key, out var slot) ? slot.Cts : null;
        cts?.Cancel();
    }

    public void DismissError(Key key)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Error is null)
                return;
            slot.Error = null;
        }

        Changed?.Invoke(key);
    }

    public void DismissOffer(Key key)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Offer is null)
                return;
            slot.Offer = null;
        }

        Changed?.Invoke(key);
    }

    /// <summary>Keeps a commit-job failure for this context when no click-run is already using the slot.</summary>
    public void RememberError(Key key, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;

        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var existing) && existing.Cts is not null)
                return;
            var slot = existing ?? new Slot();
            slot.Error = error;
            slot.Offer = null;
            _slots[key] = slot;
        }

        Changed?.Invoke(key);
    }

    private async Task FinishAsync(
        Key key,
        CancellationTokenSource cts,
        Func<CancellationToken, Task<Outcome>> run,
        Func<Outcome, Task<AiApplyDecision?>>? consult)
    {
        Outcome outcome;
        try
        {
            outcome = await run(cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.CancelledResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Commit message generation failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
            outcome = Outcome.Failed("Writing the commit message failed - see logs.", "", "");
        }

        if (outcome.Cancelled || cts.IsCancellationRequested)
        {
            EndRun(key, cts);
            cts.Dispose();
            return;
        }

        AiApplyDecision? decision = null;
        var pageApplied = false;
        if (outcome.Success && !string.IsNullOrEmpty(outcome.Message))
        {
            if (consult is not null)
            {
                try
                {
                    decision = await consult(outcome);
                    // A page that is still showing this context applies a direct result itself, on its renderer,
                    // in the same turn as the scope check. Writing it again here could replace a keystroke.
                    pageApplied = decision is { ApplyDirectly: true };
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Changes was gone when the commit message finished. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
                }
            }

            // No page is showing this context: the stored message is the box. A scope check needs the live tree.
            decision ??= AiTextApplyPolicy.Decide(
                outcome.MessageAtClick,
                messages.Get(key.WorkspaceId, key.ContextId),
                scopeChanged: false,
                AiTextApplyPolicy.StaleChangesReason);
        }

        var published = false;
        lock (_gate)
        {
            if (SameRun(key, cts))
            {
                var slot = _slots[key];
                slot.Cts = null;
                if (decision is null || string.IsNullOrEmpty(outcome.Message))
                {
                    slot.Error = string.IsNullOrWhiteSpace(outcome.Error)
                        ? "Writing the commit message failed - see logs."
                        : outcome.Error;
                    slot.Offer = null;
                }
                else
                {
                    slot.Error = null;
                    if (decision.ApplyDirectly)
                    {
                        if (!pageApplied)
                            messages.Set(key.WorkspaceId, key.ContextId, outcome.Message);
                        slot.Offer = null;
                    }
                    else
                    {
                        slot.Offer = new Offer(outcome.Message, outcome.ProviderLabel, decision.Reason);
                    }
                }

                published = true;
            }
        }

        cts.Dispose();
        if (published)
            Changed?.Invoke(key);
    }

    private void EndRun(Key key, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (!SameRun(key, cts))
                return;
            _slots[key].Cts = null;
        }

        Changed?.Invoke(key);
    }

    private bool SameRun(Key key, CancellationTokenSource cts) =>
        _slots.TryGetValue(key, out var slot) && ReferenceEquals(slot.Cts, cts);
}
