namespace GrayMoon.App.Models;

public enum WorkspaceFeatureRepositoryState
{
    Pending = 0,
    Ready = 1,
    NeedsRepair = 2,
    /// <summary>Remove is in progress for this repository; its worktree is not yet confirmed unregistered.</summary>
    Removing = 3,
    /// <summary>This repository's worktree has been unregistered by Remove. Never a live worktree.</summary>
    Removed = 4
}
