using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private ConfirmModalState _confirmModal = new();
    private DefaultBranchWarningModalState _defaultBranchWarningModal = new();
    private SyncToDefaultOptionsModalState _syncToDefaultOptionsModal = new();
    private OperationErrorModalState _operationErrorModal = new();

    private void CloseConfirmModal()
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = false,
            ButtonText = "Yes",
            PendingAction = null,
        };
        StateHasChanged();
    }

    private async Task OnConfirmModalYesAsync()
    {
        var action = _confirmModal.PendingAction;
        CloseConfirmModal();
        if (action != null)
            await action();
    }

    private void ShowConfirm(string message, Func<Task> onConfirm, string confirmButtonText = "Yes")
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = true,
            Message = message,
            ButtonText = confirmButtonText,
            PendingAction = onConfirm,
        };
        StateHasChanged();
    }

    private void ShowDefaultBranchWarning(string message, IReadOnlyList<DefaultBranchWarningItem> repoItems, Func<Task> onProceed)
    {
        _defaultBranchWarningModal = _defaultBranchWarningModal with
        {
            IsVisible = true,
            Message = message,
            RepoItems = repoItems,
            PendingAction = onProceed,
        };
        StateHasChanged();
    }

    private void CloseDefaultBranchWarningModal()
    {
        _defaultBranchWarningModal = _defaultBranchWarningModal with
        {
            IsVisible = false,
            PendingAction = null,
        };
        StateHasChanged();
    }

    private async Task OnDefaultBranchWarningProceedAsync()
    {
        var action = _defaultBranchWarningModal.PendingAction;
        CloseDefaultBranchWarningModal();
        if (action != null)
            await action();
    }

    private void ShowSyncToDefaultOptions(string message, IReadOnlyList<SyncToDefaultRepoItem> repoItems, Func<bool, bool, Task> onProceed, bool defaultDeleteRemote = true)
    {
        _syncToDefaultOptionsModal = _syncToDefaultOptionsModal with
        {
            IsVisible = true,
            Message = message,
            RepoItems = repoItems,
            DeleteRemoteBranches = defaultDeleteRemote,
            AllowForceDeleteLocalBranch = true,
            PendingAction = onProceed
        };
        StateHasChanged();
    }

    private void CloseSyncToDefaultOptionsModal()
    {
        _syncToDefaultOptionsModal = _syncToDefaultOptionsModal with { IsVisible = false, PendingAction = null };
        StateHasChanged();
    }

    private async Task OnSyncToDefaultOptionsProceedAsync()
    {
        var action = _syncToDefaultOptionsModal.PendingAction;
        var deleteRemote = _syncToDefaultOptionsModal.DeleteRemoteBranches;
        var allowForce = _syncToDefaultOptionsModal.AllowForceDeleteLocalBranch;
        CloseSyncToDefaultOptionsModal();
        if (action != null)
            await action(deleteRemote, allowForce);
    }

    private void ShowOperationError(string title, string message)
    {
        _operationErrorModal = _operationErrorModal with { IsVisible = true, Title = title, Message = message };
        _ = InvokeAsync(StateHasChanged);
    }

    private void CloseOperationErrorModal()
    {
        _operationErrorModal = _operationErrorModal with { IsVisible = false };
        StateHasChanged();
    }

    private sealed record ConfirmModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public string ButtonText { get; init; } = "Yes";
        public Func<Task>? PendingAction { get; init; }
    }

    private sealed record DefaultBranchWarningModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<DefaultBranchWarningItem> RepoItems { get; init; } = Array.Empty<DefaultBranchWarningItem>();
        public Func<Task>? PendingAction { get; init; }
    }

    private sealed record SyncToDefaultOptionsModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<SyncToDefaultRepoItem> RepoItems { get; init; } = Array.Empty<SyncToDefaultRepoItem>();
        public bool DeleteRemoteBranches { get; init; } = true;
        public bool AllowForceDeleteLocalBranch { get; init; } = true;
        public Func<bool, bool, Task>? PendingAction { get; init; }
    }

    private sealed record OperationErrorModalState
    {
        public bool IsVisible { get; init; }
        public string Title { get; init; } = "Operation Failed";
        public string Message { get; init; } = "";
    }
}
