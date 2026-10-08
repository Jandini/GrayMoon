namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Lets a GrayMoon-managed bulk operation (Sync) tell the Git Changes refresh machinery that it is about to
/// change a repository's <c>.git</c> metadata itself, so the file watcher events that change causes do not each
/// schedule their own <c>git status</c>. Suppression is per repository and reference counted: overlapping scopes
/// on one repository release together, and one repository finishing never releases another.
/// </summary>
public interface IGitChangesRefreshSuppressor
{
    /// <summary>
    /// Starts a scope during which watcher-originated refreshes for <paramref name="repoPath"/> are coalesced into
    /// a dirty flag. Disposing the last open scope schedules exactly one debounced authoritative refresh when any
    /// watcher event was coalesced. Manual refreshes (<c>RefreshNowAsync</c>) are never suppressed. Dispose is
    /// idempotent and must run on every exit path (success, failure, cancellation).
    /// </summary>
    IDisposable BeginExternalRepositoryMutation(string repoPath);
}
