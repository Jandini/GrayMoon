using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Ai;
using GrayMoon.Common.Ai;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// The AI segment of the Commit control: writes the one shared commit message for the commit the Commit button
/// currently offers. Clicking the provider segment only fills the message box. That write keeps going if the page
/// changes or the Feature context changes, and a Feature and the Workspace can be written at the same time.
/// With the menu's "Generate commit message" ticked, Commit with an empty box also writes the message first, as the
/// opening step of the commit job (see <see cref="GenerateCommitMessageInJobAsync"/>); a message the user typed is
/// always committed as typed.
/// </summary>
public sealed partial class WorkspaceGitChanges
{
    private const string AiTerminalLabel = "ai";

    [Inject] private IWorkspaceAiGenerationOperations AiOperations { get; set; } = default!;
    [Inject] private WorkspaceGitChangesGenerateMessageOnCommitMemory GenerateMessageOnCommitMemory { get; set; } = default!;
    [Inject] private WorkspaceGitChangesCommitGeneration CommitGeneration { get; set; } = default!;

    private AiProviderMenu? _aiMenu;
    private string? _aiLocalError;

    /// <summary>The menu's "Generate commit message" option. Starts ticked until the user changes it, then remembered per workspace like "Push committed".</summary>
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

    private WorkspaceGitChangesCommitGeneration.Key? CurrentCommitGenerationKey =>
        _selectedContextId is { } contextId
            ? new WorkspaceGitChangesCommitGeneration.Key(WorkspaceId, contextId.Value)
            : null;

    private bool _aiGenerating =>
        CurrentCommitGenerationKey is { } key && CommitGeneration.IsRunning(key);

    private string? _aiError =>
        _aiLocalError ?? (CurrentCommitGenerationKey is { } key ? CommitGeneration.Error(key) : null);

    private WorkspaceGitChangesCommitGeneration.Offer? _aiSuggestion =>
        CurrentCommitGenerationKey is { } key ? CommitGeneration.OfferFor(key) : null;

    /// <summary>
    /// The provider that writes the message when Commit is pressed with an empty box: only with the option ticked, a
    /// pinned provider that is available, and no generation from the provider segment already in flight for this
    /// context. Null otherwise.
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
    /// outcome). The message lands in that context's box and memory before anything is committed, so if the commit then
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
            CommitGeneration.RememberError(new WorkspaceGitChangesCommitGeneration.Key(scope.WorkspaceId, scope.ContextId.Value), reason);
            SafeInvoke(() => ToastService.ShowError($"{provider.Label} could not write the commit message, so nothing was committed."));
            return null;
        }

        var message = result.Value!.Message;
        terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stdout, $"Commit message by {provider.Label}:");
        foreach (var line in message.Split('\n'))
            terminal.Append(AiTerminalLabel, WorkerCommandStreamKind.Stdout, $"  {line.TrimEnd('\r')}");

        // Memory first: SafeInvoke no-ops once the page is gone, and the message must survive navigating away mid-job.
        CommitMessageMemory.Set(scope.WorkspaceId, scope.ContextId.Value, message);
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

    /// <summary>A workspace switch reloads the provider menu. An in-flight write for the previous scope keeps running.</summary>
    private void ResetAiForScopeChange(bool reloadMenu)
    {
        _aiLocalError = null;
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

    private void CancelAiGeneration()
    {
        if (CurrentCommitGenerationKey is { } key)
            CommitGeneration.Cancel(key);
    }

    private void OnCommitGenerationChanged(WorkspaceGitChangesCommitGeneration.Key key)
    {
        if (_disposed || CurrentCommitGenerationKey is not { } current || current != key)
            return;

        _ = InvokeAsync(() =>
        {
            if (_disposed || CurrentCommitGenerationKey != key)
                return;

            if (!CommitGeneration.IsRunning(key) && CommitGeneration.OfferFor(key) is null)
            {
                var stored = CommitMessageMemory.Get(key.WorkspaceId, key.ContextId);
                if (!string.Equals(stored, _workspaceCommitMessage, StringComparison.Ordinal))
                    ShowCommitMessage(stored);
            }

            StateHasChanged();
        });
    }

    /// <summary>
    /// One click, one generation, for the Feature context or Workspace on screen. The run is not tied to this page:
    /// leaving Changes or switching context does not cancel it, and the other context can be written at the same time.
    /// </summary>
    private Task GenerateCommitMessageAsync()
    {
        if (_aiGenerating || _aiMenu?.Pinned is not { IsAvailable: true } provider || _view is null || CurrentCommitGenerationKey is not { } key)
            return Task.CompletedTask;

        var mode = OfferedCommitMode;
        var targets = _view.Repositories
            .Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount))
            .ToList();
        if (targets.Count == 0)
        {
            _aiLocalError = "There is nothing to commit.";
            return Task.CompletedTask;
        }

        var conflicted = targets.Where(r => r.ConflictCount > 0).Select(r => r.RepositoryName).ToList();
        if (conflicted.Count > 0)
        {
            _aiLocalError = $"Resolve the merge conflicts first ({string.Join(", ", conflicted)}).";
            return Task.CompletedTask;
        }

        var messageAtClick = _workspaceCommitMessage;
        var fingerprint = AiTextApplyPolicy.CommitScopeFingerprint(mode, targets);
        var scope = BuildAiCommitScope(mode, targets);
        _aiLocalError = null;

        CommitGeneration.TryStart(
            key,
            async ct =>
            {
                try
                {
                    var result = await ScopedExecutor.ExecuteAsync<IWorkspaceAiGenerationOperations, AiGenerationResult<GeneratedCommitMessage>>(
                        ops => ops.GenerateCommitMessageAsync(scope, provider.Id, progress: null, ct),
                        ct);
                    return result.Success
                        ? WorkspaceGitChangesCommitGeneration.Outcome.Succeeded(result.Value!.Message, provider.Label, messageAtClick)
                        : WorkspaceGitChangesCommitGeneration.Outcome.Failed(result.Failure!.Message, provider.Label, messageAtClick);
                }
                catch (OperationCanceledException)
                {
                    return WorkspaceGitChangesCommitGeneration.Outcome.CancelledResult;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "AI commit message failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", key.WorkspaceId, key.ContextId);
                    return WorkspaceGitChangesCommitGeneration.Outcome.Failed("Writing the commit message failed - see logs.", provider.Label, messageAtClick);
                }
            },
            outcome => ConsultCommitApplyAsync(key, outcome, mode, fingerprint));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The live scope check, only while this page is still showing the context that was clicked. Otherwise null, and
    /// the result is stored from that context's message memory.
    /// </summary>
    private async Task<AiApplyDecision?> ConsultCommitApplyAsync(
        WorkspaceGitChangesCommitGeneration.Key key,
        WorkspaceGitChangesCommitGeneration.Outcome outcome,
        WorkspaceCommitMode mode,
        string fingerprint)
    {
        if (_disposed || CurrentCommitGenerationKey != key)
            return null;

        AiApplyDecision? decision = null;
        await InvokeAsync(() =>
        {
            if (_disposed || CurrentCommitGenerationKey != key)
                return;

            var current = (_view?.Repositories ?? []).Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount));
            var scopeChanged = OfferedCommitMode != mode
                               || !string.Equals(fingerprint, AiTextApplyPolicy.CommitScopeFingerprint(mode, current), StringComparison.Ordinal);
            decision = AiTextApplyPolicy.Decide(outcome.MessageAtClick, _workspaceCommitMessage, scopeChanged, AiTextApplyPolicy.StaleChangesReason);
            if (decision.ApplyDirectly && outcome.Message is not null)
                ApplyCommitMessage(outcome.Message);
        });
        return decision;
    }

    private void ShowCommitMessage(string message)
    {
        _workspaceCommitMessage = message;
        _workspaceCommitMessageHasContent = !string.IsNullOrWhiteSpace(message);
        _ignoreStaleCommitMessage = null;
        // A new key re-creates the textarea so it shows the new value (its value is not two-way bound).
        _commitMessageBoxKey++;
    }

    private void ApplyCommitMessage(string message)
    {
        ShowCommitMessage(message);
        if (_selectedContextId is { } contextId)
            CommitMessageMemory.Set(WorkspaceId, contextId.Value, message);
    }

    private void ReplaceWithAiSuggestion()
    {
        if (_aiSuggestion is not { } suggestion)
            return;
        ApplyCommitMessage(suggestion.Message);
        if (CurrentCommitGenerationKey is { } key)
            CommitGeneration.DismissOffer(key);
    }

    private void DismissAiSuggestion()
    {
        if (CurrentCommitGenerationKey is { } key)
            CommitGeneration.DismissOffer(key);
    }

    private void DismissAiError()
    {
        _aiLocalError = null;
        if (CurrentCommitGenerationKey is { } key && CommitGeneration.Error(key) is not null)
        {
            CommitGeneration.DismissError(key);
            return;
        }

        StateHasChanged();
    }

    /// <summary>A commit started with an empty box: the provider and the exact scope captured when the commit started.</summary>
    private sealed record CommitMessageGeneration(AiProviderOption Provider, AiCommitScope Scope);
}
