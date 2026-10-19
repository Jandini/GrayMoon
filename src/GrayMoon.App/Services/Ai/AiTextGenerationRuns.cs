using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.Ai;

/// <summary>
/// App-wide (singleton) runs of one AI text, started from a provider button. One run per Workspace and Feature context,
/// so a Feature and the Workspace can be written at the same time. Leaving the page, closing the dialog, switching
/// context or reloading (F5 / Ctrl+F5 starts a new circuit) does not cancel a run; only Cancel for that same context
/// does. The written text is stored for that context even when no page is showing it, and an error or a suggestion
/// stays for that context until dismissed. Not kept across an App restart.
/// The work itself must not use the starting circuit's scope (start it through <c>IScopedServiceExecutor</c>).
/// </summary>
/// <typeparam name="TText">The generated text, as the derived class stores it.</typeparam>
public abstract class AiTextGenerationRuns<TText>(ILogger logger, string failedMessage)
    where TText : class
{
    public readonly record struct Key(int WorkspaceId, int ContextId);

    public sealed record Offer(TText Text, string ProviderLabel, string? Reason);

    /// <summary>A finished run. <see cref="TextAtClick"/> is what the user had written when they clicked, compared like <see cref="CurrentText"/>.</summary>
    public sealed record Outcome(
        bool Cancelled,
        bool Success,
        TText? Text,
        string? Error,
        string ProviderLabel,
        string TextAtClick)
    {
        public static Outcome CancelledResult { get; } = new(true, false, null, null, "", "");

        public static Outcome Succeeded(TText text, string providerLabel, string textAtClick) =>
            new(false, true, text, null, providerLabel, textAtClick);

        public static Outcome Failed(string error, string providerLabel, string textAtClick) =>
            new(false, false, null, error, providerLabel, textAtClick);
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

    /// <summary>What the user has written for <paramref name="key"/> when no page is showing it, compared with <see cref="Outcome.TextAtClick"/>.</summary>
    protected abstract string CurrentText(Key key);

    /// <summary>Puts the text in that context's memory, as if it had been applied to the field.</summary>
    protected abstract void Store(Key key, TText text);

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
    /// <paramref name="key"/> and the text is stored from memory instead. When it returns apply-directly, the page
    /// has already put the text in the field.
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

    /// <summary>Keeps a failure from outside a click-run for this context when no click-run is already using the slot.</summary>
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
            logger.LogError(ex, "AI text generation failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
            outcome = Outcome.Failed(failedMessage, "", "");
        }

        if (outcome.Cancelled || cts.IsCancellationRequested)
        {
            EndRun(key, cts);
            cts.Dispose();
            return;
        }

        AiApplyDecision? decision = null;
        var pageApplied = false;
        if (outcome.Success && outcome.Text is not null)
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
                    logger.LogDebug(ex, "The page was gone when the AI text finished. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
                }
            }

            // No page is showing this context: the stored text is the field. A scope check needs the live page.
            decision ??= AiTextApplyPolicy.Decide(
                outcome.TextAtClick,
                CurrentText(key),
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
                if (decision is null || outcome.Text is null)
                {
                    slot.Error = string.IsNullOrWhiteSpace(outcome.Error) ? failedMessage : outcome.Error;
                    slot.Offer = null;
                }
                else
                {
                    slot.Error = null;
                    if (decision.ApplyDirectly)
                    {
                        if (!pageApplied)
                            Store(key, outcome.Text);
                        slot.Offer = null;
                    }
                    else
                    {
                        slot.Offer = new Offer(outcome.Text, outcome.ProviderLabel, decision.Reason);
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
