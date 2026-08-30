namespace GrayMoon.App.Models;

public enum RepoSyncStatus
{
    InSync,
    NotCloned,
    VersionMismatch,
    Error,
    /// <summary>Unknown state for newly added repos (not yet cloned or version checked).</summary>
    NeedsSync
}
