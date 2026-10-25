using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Ai;
using GrayMoon.Common.Ai;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Modals;

/// <summary>
/// AI title and description for the dialog: one shared text for every pull request, from each repository's source
/// branch compared with the target selected in the Branch tab. It only fills the Details fields; branches, reviewers,
/// draft and the Create buttons are never touched.
/// The title and description are remembered per Workspace and Feature context (<see cref="WorkspacePullRequestDraftMemory"/>),
/// and a generation runs outside the dialog (<see cref="WorkspacePullRequestTextGeneration"/>): closing the dialog,
/// leaving the page or reloading does not cancel it, and its text, suggestion or error shows when the dialog opens again.
/// </summary>
public partial class NewPullRequestModal
{
    [Inject] private WorkspacePullRequestDraftMemory Drafts { get; set; } = default!;
    [Inject] private WorkspacePullRequestTextGeneration Generation { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private WorkerConnectionTracker WorkerConnectionTracker { get; set; } = default!;

    private AiProviderMenu? _aiMenu;
    private string? _aiLocalError;
    private string? _aiAppliedFingerprint;
    private WorkerConnectionState? _aiWorkerState;
    private bool _disposed;

    private WorkspacePullRequestTextGeneration.Key GenerationKey => new(WorkspaceId, ContextId.Value);

    private bool _aiGenerating => Generation.IsRunning(GenerationKey);

    private string? _aiError => _aiLocalError ?? Generation.Error(GenerationKey);

    private AiGenerationWarning? _aiWarning => Generation.Warning(GenerationKey);

    private WorkspacePullRequestTextGeneration.Offer? _aiSuggestion => Generation.OfferFor(GenerationKey);

    /// <summary>The fields hold AI text that was written for a branch selection other than the current one.</summary>
    private bool IsAiTextStale =>
        _aiAppliedFingerprint is not null
        && !string.Equals(_aiAppliedFingerprint, CurrentAiScope().Fingerprint(), StringComparison.Ordinal);

    protected override void OnInitialized()
    {
        Generation.Changed += OnGenerationChanged;
        WorkerConnectionTracker.OnStateChanged(OnAiWorkerStateChanged);
    }

    /// <summary>
    /// The menu is read from the Worker, so one read while it was offline shows only that error. When the Worker comes
    /// back online while the dialog is open the menu is read again; a closed dialog reads it when it opens. The first
    /// call is the tracker's current state and only records it.
    /// </summary>
    private void OnAiWorkerStateChanged(WorkerConnectionState state)
    {
        var previous = _aiWorkerState;
        _aiWorkerState = state;
        if (_disposed || !IsVisible || state != WorkerConnectionState.Online || previous is null or WorkerConnectionState.Online)
            return;

        _ = InvokeAsync(LoadAiMenuAsync);
    }

    /// <summary>Shows this context's remembered title and description, if any. A running generation is left running.</summary>
    private void RestoreDraftOnOpen()
    {
        _aiLocalError = null;
        _aiAppliedFingerprint = null;
        if (Drafts.Get(WorkspaceId, ContextId.Value) is { } draft)
            ShowDraft(draft);

        _aiMenu = null;
        _ = LoadAiMenuAsync();
    }

    private void ShowDraft(PullRequestDraft draft)
    {
        _title = draft.TitleIsDefault ? ComputeDefaultTitle() : draft.Title;
        _body = draft.Body;
        _aiAppliedFingerprint = draft.AiFingerprint;
    }

    /// <summary>Remembers the fields for this context, so they outlive the dialog, the page and a reload.</summary>
    private void SaveDraft() => Drafts.Set(WorkspaceId, ContextId.Value, CurrentDraft());

    private PullRequestDraft CurrentDraft() => new(_title, _body, IsTitleDefault, _aiAppliedFingerprint);

    private bool IsTitleDefault => string.Equals(_title, ComputeDefaultTitle(), StringComparison.Ordinal);

    private async Task LoadAiMenuAsync()
    {
        try
        {
            _aiMenu = await AiOperations.GetProvidersAsync(WorkspaceId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Loading AI providers failed. WorkspaceId={WorkspaceId}", WorkspaceId);
            _aiMenu = AiProviderMenu.Empty;
        }

        if (!_disposed)
            await InvokeAsync(StateHasChanged);
    }

    private async Task OnAiPinAsync(string? providerId)
    {
        if (providerId is not null && string.Equals(_aiMenu?.PinnedProviderId, providerId, StringComparison.Ordinal))
            providerId = null;

        try
        {
            await AiOperations.PinProviderAsync(WorkspaceId, providerId, CancellationToken.None);
            if (_aiMenu is not null)
                _aiMenu = _aiMenu with { PinnedProviderId = providerId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Pinning the AI provider failed. WorkspaceId={WorkspaceId}", WorkspaceId);
            _aiLocalError = "Could not pin the AI provider.";
        }
    }

    private void CancelAiGeneration() => Generation.Cancel(GenerationKey);

    /// <summary>The comparison the Create request would use right now: each source with its selected target.</summary>
    private AiPullRequestScope CurrentAiScope() => new(
        WorkspaceId,
        ContextId,
        Targets.Select(t => new AiPullRequestRepository(t.RepositoryId, t.HeadBranch, GetRepoBranchState(t.RepositoryId).SelectedBase)).ToList());

    /// <summary>What the user wrote: the prefilled default title alone counts as nothing written yet.</summary>
    private string UserWrittenText() => PullRequestDraft.UserWritten(IsTitleDefault ? string.Empty : _title, _body);

    /// <summary>
    /// One click, one generation, for the Feature context or Workspace the dialog was opened for. The run is not tied
    /// to the dialog: closing it, leaving the page or reloading does not cancel it.
    /// </summary>
    private Task GeneratePullRequestTextAsync()
    {
        if (_aiGenerating || IsBusy || _aiMenu?.Pinned is not { IsAvailable: true } provider)
            return Task.CompletedTask;

        if (_branchByRepoId.Values.Any(s => s.IsLoading))
        {
            _aiLocalError = "Wait for the target branches to load.";
            _activeTab = "details";
            return Task.CompletedTask;
        }

        var invalid = Targets.Where(t =>
        {
            var state = GetRepoBranchState(t.RepositoryId);
            return state.Candidates.Count == 0
                   || string.IsNullOrWhiteSpace(state.SelectedBase)
                   || string.Equals(state.SelectedBase, t.HeadBranch, StringComparison.Ordinal);
        }).Select(t => t.RepositoryName).ToList();
        if (invalid.Count > 0)
        {
            _aiLocalError = $"Choose a valid target branch first ({string.Join(", ", invalid)}).";
            _activeTab = "details";
            return Task.CompletedTask;
        }

        var key = GenerationKey;
        var scope = CurrentAiScope();
        var fingerprint = scope.Fingerprint();
        var textAtClick = UserWrittenText();
        _aiLocalError = null;
        // The memory is the field once the dialog is gone; it must hold what the user had at the click.
        SaveDraft();

        Generation.TryStart(
            key,
            async ct =>
            {
                try
                {
                    var result = await ScopedExecutor.ExecuteAsync<IWorkspaceAiGenerationOperations, AiGenerationResult<GeneratedPullRequestText>>(
                        ops => ops.GeneratePullRequestTextAsync(scope, provider.Id, ct),
                        ct);
                    return result.Success
                        ? WorkspacePullRequestTextGeneration.Outcome.Succeeded(new PullRequestAiText(result.Value!, fingerprint), provider.Label, textAtClick, result.Warning)
                        : WorkspacePullRequestTextGeneration.Outcome.Failed(result.Failure!.Message, provider.Label, textAtClick);
                }
                catch (OperationCanceledException)
                {
                    return WorkspacePullRequestTextGeneration.Outcome.CancelledResult;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "AI pull request text failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
                    return WorkspacePullRequestTextGeneration.Outcome.Failed("Writing the title and description failed - see logs.", provider.Label, textAtClick);
                }
            },
            outcome => ConsultPullRequestApplyAsync(key, outcome));
        return Task.CompletedTask;
    }

    private bool IsShowing(WorkspacePullRequestTextGeneration.Key key) => !_disposed && IsVisible && GenerationKey == key;

    /// <summary>
    /// The live branch-selection check, only while this dialog is still open for the context that was clicked.
    /// Otherwise null, and the result is stored from that context's draft memory.
    /// </summary>
    private async Task<AiApplyDecision?> ConsultPullRequestApplyAsync(
        WorkspacePullRequestTextGeneration.Key key,
        WorkspacePullRequestTextGeneration.Outcome outcome)
    {
        if (!IsShowing(key) || outcome.Text is not { } generated)
            return null;

        AiApplyDecision? decision = null;
        await InvokeAsync(() =>
        {
            if (!IsShowing(key))
                return;

            var scopeChanged = !string.Equals(generated.Fingerprint, CurrentAiScope().Fingerprint(), StringComparison.Ordinal);
            decision = AiTextApplyPolicy.Decide(outcome.TextAtClick, UserWrittenText(), scopeChanged, AiTextApplyPolicy.StaleBranchesReason);
            if (decision.ApplyDirectly)
                ApplyPullRequestText(generated);
        });
        return decision;
    }

    private void OnGenerationChanged(WorkspacePullRequestTextGeneration.Key key)
    {
        if (!IsShowing(key))
            return;

        _ = InvokeAsync(() =>
        {
            if (!IsShowing(key))
                return;

            if (!Generation.IsRunning(key))
            {
                // A run started before a reload, or finished while this dialog was closed, lands in the memory only.
                if (Generation.OfferFor(key) is null
                    && Drafts.Get(key.WorkspaceId, key.ContextId) is { } draft
                    && draft != CurrentDraft())
                    ShowDraft(draft);

                if (Generation.OfferFor(key) is not null || Generation.Error(key) is not null)
                    _activeTab = "details";
            }

            StateHasChanged();
        });
    }

    private void ApplyPullRequestText(PullRequestAiText text)
    {
        _title = text.Text.Title;
        _body = text.Text.Description;
        _aiAppliedFingerprint = text.Fingerprint;
        _activeTab = "details";
        SaveDraft();
    }

    private void ReplaceWithAiSuggestion()
    {
        if (_aiSuggestion is not { } suggestion)
            return;
        ApplyPullRequestText(suggestion.Text);
        Generation.DismissOffer(GenerationKey);
    }

    private void DismissAiSuggestion() => Generation.DismissOffer(GenerationKey);

    private void DismissAiWarning()
    {
        if (Generation.Warning(GenerationKey) is not null)
            Generation.DismissWarning(GenerationKey);
        else
            StateHasChanged();
    }

    private void DismissAiError()
    {
        if (_aiLocalError is not null)
        {
            _aiLocalError = null;
            return;
        }

        Generation.DismissError(GenerationKey);
    }

    private void DisposeAi()
    {
        _disposed = true;
        Generation.Changed -= OnGenerationChanged;
        WorkerConnectionTracker.RemoveStateChanged(OnAiWorkerStateChanged);
    }
}
