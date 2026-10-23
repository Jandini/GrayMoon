using GrayMoon.App.Components.Modals;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Global preference for the "Return to default branch" checkbox in <see cref="Components.Modals.MergePullRequestModal"/>
/// and <see cref="Components.Modals.BulkMergePullRequestsModal"/>. Remembers the user's last choice across dialog
/// opens and page navigation (singleton, so it survives for the lifetime of the app/circuit rather than resetting
/// every time the dialog is reopened). <see cref="ReturnToDefaultScope"/> is only used by the bulk-merge dialog.
/// </summary>
public sealed class MergePullRequestReturnToDefaultPreferenceService
{
    public bool ReturnToDefault { get; set; } = false;

    /// <summary>When <see cref="ReturnToDefault"/> is on in the bulk-merge dialog: return only merged repositories, or every repository in the merge scope (including those with no open PR).</summary>
    public BulkMergeReturnToDefaultScope ReturnToDefaultScope { get; set; } = BulkMergeReturnToDefaultScope.Merged;
}
