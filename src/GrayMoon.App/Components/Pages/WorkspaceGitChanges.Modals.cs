using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private DefaultBranchWarningModalState _defaultBranchWarningModal = new();

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

    private sealed record DefaultBranchWarningModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<DefaultBranchWarningItem> RepoItems { get; init; } = Array.Empty<DefaultBranchWarningItem>();
        public Func<Task>? PendingAction { get; init; }
    }
}
