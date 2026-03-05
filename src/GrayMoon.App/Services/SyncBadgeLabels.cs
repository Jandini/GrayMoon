using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>Display text for RepoSyncStatus badges.</summary>
public static class SyncBadgeLabels
{
    public const string DefaultBranchName = "main";

    public static string GetSyncBadgeText(RepoSyncStatus status)
    {
        return status switch
        {
            RepoSyncStatus.InSync => "in sync",
            RepoSyncStatus.NeedsSync => "sync",
            RepoSyncStatus.NotCloned => "not cloned",
            RepoSyncStatus.VersionMismatch => "version",
            RepoSyncStatus.Error => "error",
            _ => "sync"
        };
    }
}
