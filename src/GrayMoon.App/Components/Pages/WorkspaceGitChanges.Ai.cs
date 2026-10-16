using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Ai;
using GrayMoon.Common.Ai;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// The AI segment of the Commit control: writes the one shared commit message for the commit the Commit button
/// currently offers. It only ever fills the message box; staging, committing, pushing and the Push committed choice stay
/// with the existing Commit flow, which keeps working whatever the AI does.
/// </summary>
public sealed partial class WorkspaceGitChanges
{
    [Inject] private IWorkspaceAiGenerationOperations AiOperations { get; set; } = default!;

    private AiProviderMenu? _aiMenu;
    private bool _aiGenerating;
    private CancellationTokenSource? _aiCts;
    private string? _aiError;
    private CommitSuggestion? _aiSuggestion;

    private sealed record CommitSuggestion(string Message, string ProviderLabel, string? Reason);

    private async Task LoadAiMenuAsync(bool refresh = false)
    {
        var workspaceId = WorkspaceId;
        try
        {
            var menu = await AiOperations.GetProvidersAsync(workspaceId, refresh, CancellationToken.None);
            if (!_disposed && workspaceId == WorkspaceId)
            {
                _aiMenu = menu;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Loading AI providers failed. WorkspaceId={WorkspaceId}", workspaceId);
            if (!_disposed && workspaceId == WorkspaceId)
            {
                _aiMenu = AiProviderMenu.Empty;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    /// <summary>A workspace or context switch: whatever was being written belongs to the previous scope.</summary>
    private void ResetAiForScopeChange(bool reloadMenu)
    {
        _aiCts?.Cancel();
        _aiCts = null;
        _aiGenerating = false;
        _aiError = null;
        _aiSuggestion = null;
        if (reloadMenu)
        {
            _aiMenu = null;
            _ = LoadAiMenuAsync();
        }
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
            ToastService.ShowError("Could not pin the AI provider.");
        }
    }

    private Task OnAiRefreshAsync()
    {
        _aiMenu = null;
        return LoadAiMenuAsync(refresh: true);
    }

    private void CancelAiGeneration() => _aiCts?.Cancel();

    /// <summary>
    /// One click, one generation. Captures the commit the button offers right now (mode and target repositories), the
    /// message as it is, and the scope's identity; the result is applied only if none of them changed.
    /// </summary>
    private async Task GenerateCommitMessageAsync()
    {
        if (_aiGenerating || _aiMenu?.Pinned is not { IsAvailable: true } provider || _view is null)
            return;

        var mode = OfferedCommitMode;
        var targets = _view.Repositories
            .Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount))
            .ToList();
        if (targets.Count == 0)
        {
            _aiError = "There is nothing to commit.";
            return;
        }

        var conflicted = targets.Where(r => r.ConflictCount > 0).Select(r => r.RepositoryName).ToList();
        if (conflicted.Count > 0)
        {
            _aiError = $"Resolve the merge conflicts first ({string.Join(", ", conflicted)}).";
            return;
        }

        var workspaceId = WorkspaceId;
        var contextId = RequireSelectedContextId();
        var messageAtClick = _workspaceCommitMessage;
        var fingerprint = AiTextApplyPolicy.CommitScopeFingerprint(mode, targets);
        var scope = new AiCommitScope(
            workspaceId,
            contextId,
            mode == WorkspaceCommitMode.Staged ? GitCommitEvidenceMode.Staged : GitCommitEvidenceMode.All,
            targets.Select(r => new AiCommitRepository(r.RepositoryId, r.BranchName)).ToList());

        using var cts = new CancellationTokenSource();
        _aiCts = cts;
        _aiGenerating = true;
        _aiError = null;
        _aiSuggestion = null;
        StateHasChanged();

        try
        {
            var result = await AiOperations.GenerateCommitMessageAsync(scope, provider.Id, cts.Token);
            if (_disposed || cts.IsCancellationRequested || workspaceId != WorkspaceId || _selectedContextId != contextId)
                return;

            if (!result.Success)
            {
                _aiError = result.Failure!.Message;
                return;
            }

            var current = (_view?.Repositories ?? []).Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount));
            var scopeChanged = OfferedCommitMode != mode
                               || !string.Equals(fingerprint, AiTextApplyPolicy.CommitScopeFingerprint(mode, current), StringComparison.Ordinal);
            var decision = AiTextApplyPolicy.Decide(messageAtClick, _workspaceCommitMessage, scopeChanged, AiTextApplyPolicy.StaleChangesReason);
            if (decision.ApplyDirectly)
                ApplyCommitMessage(result.Value!.Message);
            else
                _aiSuggestion = new CommitSuggestion(result.Value!.Message, provider.Label, decision.Reason);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or by a scope change; nothing to show.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI commit message failed. WorkspaceId={WorkspaceId}", workspaceId);
            if (!_disposed)
                _aiError = "Writing the commit message failed - see logs.";
        }
        finally
        {
            if (ReferenceEquals(_aiCts, cts))
            {
                _aiCts = null;
                _aiGenerating = false;
            }

            if (!_disposed)
                StateHasChanged();
        }
    }

    private void ApplyCommitMessage(string message)
    {
        _workspaceCommitMessage = message;
        _workspaceCommitMessageHasContent = !string.IsNullOrWhiteSpace(message);
        _ignoreStaleCommitMessage = null;
        CommitMessageMemory.Set(WorkspaceId, message);
        // A new key re-creates the textarea so it shows the new value (its value is not two-way bound).
        _commitMessageBoxKey++;
    }

    private void ReplaceWithAiSuggestion()
    {
        if (_aiSuggestion is null)
            return;
        ApplyCommitMessage(_aiSuggestion.Message);
        _aiSuggestion = null;
    }

    private void DismissAiSuggestion() => _aiSuggestion = null;

    private void DismissAiError() => _aiError = null;
}
