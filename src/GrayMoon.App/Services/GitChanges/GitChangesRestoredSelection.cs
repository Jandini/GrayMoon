namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// What happens to the file Changes selected by itself (the remembered file) once its diff request ends.
/// </summary>
public static class GitChangesRestoredSelection
{
    /// <summary>
    /// True when the restored selection is dropped, so the page shows "Select a file" instead of a stuck error pane.
    /// Only a diff that failed for the restored file itself drops it. When the user chose another file while the
    /// restored diff was loading, that request was superseded: the selection is the user's now, its diff may still be
    /// loading, and it is never cleared here.
    /// </summary>
    /// <param name="superseded">A newer diff request started, or the selected row is no longer the restored one.</param>
    /// <param name="diffFailed">The request ended with an error or without a document.</param>
    public static bool ShouldDrop(bool superseded, bool diffFailed) => !superseded && diffFailed;
}
