namespace GrayMoon.App.Services;

/// <summary>
/// Global preference for the "Sync to default branch" checkbox in <see cref="Components.Modals.MergePullRequestModal"/>.
/// Remembers the user's last choice across dialog opens and page navigation (singleton, so it survives for the
/// lifetime of the app/circuit rather than resetting every time the dialog is reopened).
/// </summary>
public sealed class MergePullRequestSyncToDefaultPreferenceService
{
    public bool SyncToDefault { get; set; }
}
