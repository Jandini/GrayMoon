using GrayMoon.App.Services.Ai;
using GrayMoon.Application.Ai;
using GrayMoon.Common.Ai;

namespace GrayMoon.App.Components.Modals;

/// <summary>
/// AI title and description for the dialog: one shared text for every pull request, from each repository's source
/// branch compared with the target selected in the Branch tab. It only fills the Details fields; branches, reviewers,
/// draft and the Create buttons are never touched.
/// </summary>
public partial class NewPullRequestModal
{
    private AiProviderMenu? _aiMenu;
    private bool _aiGenerating;
    private CancellationTokenSource? _aiCts;
    private string? _aiError;
    private PullRequestSuggestion? _aiSuggestion;
    private string? _aiAppliedFingerprint;

    private sealed record PullRequestSuggestion(GeneratedPullRequestText Text, string ProviderLabel, string? Reason, string Fingerprint);

    /// <summary>The fields hold AI text that was written for a branch selection other than the current one.</summary>
    private bool IsAiTextStale =>
        _aiAppliedFingerprint is not null
        && !string.Equals(_aiAppliedFingerprint, CurrentAiScope().Fingerprint(), StringComparison.Ordinal);

    private void ResetAiOnOpen()
    {
        _aiCts?.Cancel();
        _aiCts = null;
        _aiGenerating = false;
        _aiError = null;
        _aiSuggestion = null;
        _aiAppliedFingerprint = null;
        _aiMenu = null;
        _ = LoadAiMenuAsync();
    }

    private void CancelAiOnClose()
    {
        _aiCts?.Cancel();
        _aiCts = null;
        _aiGenerating = false;
    }

    private async Task LoadAiMenuAsync(bool refresh = false)
    {
        try
        {
            _aiMenu = await AiOperations.GetProvidersAsync(WorkspaceId, refresh, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Loading AI providers failed. WorkspaceId={WorkspaceId}", WorkspaceId);
            _aiMenu = AiProviderMenu.Empty;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task OnAiPinAsync(string? providerId)
    {
        try
        {
            await AiOperations.PinProviderAsync(WorkspaceId, providerId, CancellationToken.None);
            if (_aiMenu is not null)
                _aiMenu = _aiMenu with { PinnedProviderId = providerId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Pinning the AI provider failed. WorkspaceId={WorkspaceId}", WorkspaceId);
            _aiError = "Could not pin the AI provider.";
        }
    }

    private Task OnAiRefreshAsync()
    {
        _aiMenu = null;
        return LoadAiMenuAsync(refresh: true);
    }

    private void CancelAiGeneration() => _aiCts?.Cancel();

    /// <summary>The comparison the Create request would use right now: each source with its selected target.</summary>
    private AiPullRequestScope CurrentAiScope() => new(
        WorkspaceId,
        ContextId,
        Targets.Select(t => new AiPullRequestRepository(t.RepositoryId, t.HeadBranch, GetRepoBranchState(t.RepositoryId).SelectedBase)).ToList());

    /// <summary>What the user wrote: the prefilled default title alone counts as nothing written yet.</summary>
    private string UserWrittenText() =>
        (string.Equals(_title, ComputeDefaultTitle(), StringComparison.Ordinal) ? string.Empty : _title) + "\n" + _body;

    private async Task GeneratePullRequestTextAsync()
    {
        if (_aiGenerating || IsBusy || _aiMenu?.Pinned is not { IsAvailable: true } provider)
            return;

        if (_branchByRepoId.Values.Any(s => s.IsLoading))
        {
            _aiError = "Wait for the target branches to load.";
            _activeTab = "details";
            return;
        }

        var scope = CurrentAiScope();
        var invalid = Targets.Where(t =>
        {
            var state = GetRepoBranchState(t.RepositoryId);
            return state.Candidates.Count == 0
                   || string.IsNullOrWhiteSpace(state.SelectedBase)
                   || string.Equals(state.SelectedBase, t.HeadBranch, StringComparison.Ordinal);
        }).Select(t => t.RepositoryName).ToList();
        if (invalid.Count > 0)
        {
            _aiError = $"Choose a valid target branch first ({string.Join(", ", invalid)}).";
            _activeTab = "details";
            return;
        }

        var fingerprint = scope.Fingerprint();
        var textAtClick = UserWrittenText().Trim();

        using var cts = new CancellationTokenSource();
        _aiCts = cts;
        _aiGenerating = true;
        _aiError = null;
        _aiSuggestion = null;
        StateHasChanged();

        try
        {
            var result = await AiOperations.GeneratePullRequestTextAsync(scope, provider.Id, cts.Token);
            if (cts.IsCancellationRequested || !IsVisible)
                return;

            _activeTab = "details";
            if (!result.Success)
            {
                _aiError = result.Failure!.Message;
                return;
            }

            var scopeChanged = !string.Equals(fingerprint, CurrentAiScope().Fingerprint(), StringComparison.Ordinal);
            var decision = AiTextApplyPolicy.Decide(textAtClick, UserWrittenText().Trim(), scopeChanged, AiTextApplyPolicy.StaleBranchesReason);
            if (decision.ApplyDirectly)
                ApplyPullRequestText(result.Value!, fingerprint);
            else
                _aiSuggestion = new PullRequestSuggestion(result.Value!, provider.Label, decision.Reason, fingerprint);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or by closing the dialog.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI pull request text failed. WorkspaceId={WorkspaceId}", WorkspaceId);
            _aiError = "Writing the title and description failed - see logs.";
        }
        finally
        {
            if (ReferenceEquals(_aiCts, cts))
            {
                _aiCts = null;
                _aiGenerating = false;
            }

            StateHasChanged();
        }
    }

    private void ApplyPullRequestText(GeneratedPullRequestText text, string fingerprint)
    {
        _title = text.Title;
        _body = text.Description;
        _aiAppliedFingerprint = fingerprint;
    }

    private void ReplaceWithAiSuggestion()
    {
        if (_aiSuggestion is null)
            return;
        ApplyPullRequestText(_aiSuggestion.Text, _aiSuggestion.Fingerprint);
        _aiSuggestion = null;
    }
}
