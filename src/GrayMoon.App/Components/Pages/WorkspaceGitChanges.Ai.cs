using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Ai;
using GrayMoon.Common.Ai;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// The AI segment of the Commit control: writes the one shared commit message for the commit the Commit button
/// currently offers. Clicking the provider segment only fills the message box. With the menu's "Generate commit message"
/// ticked, Commit with an empty box also writes the message first, as the opening step of the commit job (see
/// <see cref="GenerateCommitMessageInJobAsync"/>); a message the user typed is always committed as typed.
/// </summary>
public sealed partial class WorkspaceGitChanges
{
    private const string AiTerminalLabel = "ai";

    [Inject] private IWorkspaceAiGenerationOperations AiOperations { get; set; } = default!;
    [Inject] private WorkspaceGitChangesGenerateMessageOnCommitMemory GenerateMessageOnCommitMemory { get; set; } = default!;

    private AiProviderMenu? _aiMenu;
    private bool _aiGenerating;
    private CancellationTokenSource? _aiCts;
    private string? _aiError;
    private CommitSuggestion? _aiSuggestion;

    private sealed record CommitSuggestion(string Message, string ProviderLabel, string? Reason);

    /// <summary>A commit started with an empty box: the provider and the exact scope captured when the commit started.</summary>
    private sealed record CommitMessageGeneration(AiProviderOption Provider, AiCommitScope Scope);

    /// <summary>The menu's "Generate commit message" option, remembered per workspace like "Push committed".</summary>
    private bool GenerateMessageOnCommit
    {
        get => GenerateMessageOnCommitMemory.Get(WorkspaceId);
        set => GenerateMessageOnCommitMemory.Set(WorkspaceId, value);
    }

    private void OnGenerateMessageOnCommitChanged(bool value) => GenerateMessageOnCommit = value;

    private string GenerateMessageOnCommitHint =>
        _aiMenu?.Pinned is { IsAvailable: true } provider
            ? $"When empty, by {provider.Label}"
            : "Pin a provider first";

    /// <summary>
    /// The provider that writes the message when Commit is pressed with an empty box: only with the option ticked, a
    /// pinned provider that is available, and no generation from the provider segment already in flight. Null otherwise.
    /// </summary>
    private AiProviderOption? CommitMessageWriter =>
        GenerateMessageOnCommit && !_aiGenerating && _aiMenu?.Pinned is { IsAvailable: true } provider ? provider : null;

    /// <summary>True when the next Commit will write the message first (the box is empty and a writer is ready).</summary>
    private bool CommitGeneratesMessage =>
        string.IsNullOrWhiteSpace(_workspaceCommitMessage) && CommitMessageWriter is not null;

    private bool IsSameAiScope(AiCommitScope scope) => scope.WorkspaceId == WorkspaceId && scope.ContextId == _selectedContextId;

    private AiCommitScope BuildAiCommitScope(WorkspaceCommitMode mode, IEnumerable<WorkspaceGitChangesRepositoryView> targets) =>
        new(
            WorkspaceId,
            RequireSelectedContextId(),
            mode == WorkspaceCommitMode.Staged ? GitCommitEvidenceMode.Staged : GitCommitEvidenceMode.All,
            targets.Select(r => new AiCommitRepository(r.RepositoryId, r.BranchName)).ToList());

    /// <summary>
    /// The opening step of a commit job started with an empty message box: writes the message for exactly the commit's
    /// mode and repositories, echoing each step into the job's terminal (the Worker adds the provider CLI call and its
    /// outcome). The message lands in the box and the circuit memory before anything is committed, so if the commit then
    /// fails it is still there to review or retry. Returns null when nothing was written; the caller then commits nothing.
    /// </summary>
    private async Task<string?> GenerateCommitMessageInJobAsync(
        BackgroundJobHandle job,
        CommitMessageGeneration generation,
        CancellationToken cancellationToken)
    {
        var (provider, scope) = generation;
        var terminal = job.Terminal;
        var progress = new SynchronousProgress(message =>
        {
            job.ReportProgress(message);
            terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stdout, message);
        });

        var result = await AiOperations.GenerateCommitMessageAsync(scope, provider.Id, progress, cancellationToken);
        if (!result.Success)
        {
            var reason = result.Failure!.Message;
            terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stderr, $"Nothing was committed: {reason}");
            SafeInvoke(() =>
            {
                if (IsSameAiScope(scope))
                    _aiError = reason;
                ToastService.ShowError($"{provider.Label} could not write the commit message, so nothing was committed.");
            });
            return null;
        }

        var message = result.Value!.Message;
        terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stdout, $"Commit message by {provider.Label}:");
        foreach (var line in message.Split('\n'))
            terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stdout, $"  {line.TrimEnd('\r')}");

        // Memory first: SafeInvoke no-ops once the page is gone, and the message must survive navigating away mid-job.
        CommitMessageMemory.Set(scope.WorkspaceId, message);
        SafeInvoke(() =>
        {
            if (IsSameAiScope(scope))
                ApplyCommitMessage(message);
        });
        return message;
    }

    private async Task LoadAiMenuAsync()
    {
        var workspaceId = WorkspaceId;
        try
        {
            var menu = await AiOperations.GetProvidersAsync(workspaceId, CancellationToken.None);
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
            ToastService.ShowError("Could not pin the AI provider.");
        }
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
        var messageAtClick = _workspaceCommitMessage;
        var fingerprint = AiTextApplyPolicy.CommitScopeFingerprint(mode, targets);
        var scope = BuildAiCommitScope(mode, targets);

        using var cts = new CancellationTokenSource();
        _aiCts = cts;
        _aiGenerating = true;
        _aiError = null;
        _aiSuggestion = null;
        StateHasChanged();

        try
        {
            var result = await AiOperations.GenerateCommitMessageAsync(scope, provider.Id, progress: null, cts.Token);
            if (_disposed || cts.IsCancellationRequested || !IsSameAiScope(scope))
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
