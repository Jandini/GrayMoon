using GrayMoon.Abstractions.Worker;

namespace GrayMoon.Worker.Services;

/// <summary>The Worker job pools. Each is a bounded queue drained by its own hosted service.</summary>
public enum WorkerCommandPool
{
    Main,
    Read,
    Diff,
    Ai,
}

/// <summary>Chooses the pool for a command by name. Everything not listed here runs on the main pool.</summary>
public static class WorkerCommandPoolRouter
{
    /// <summary>Commands that only read repository state (never touch the index or working tree) and can
    /// run on the dedicated read pool instead of queuing behind long-running writes.</summary>
    private static readonly HashSet<string> ReadOnlyCommands =
        ["GetGitChangeStatus", "EnsureGitChangesWatchers", WorkerHubMethods.ListGitWorktrees, WorkerHubMethods.InspectWorktree,
         WorkerHubMethods.GetGitChangesMonitoring, WorkerHubMethods.ListWorkspaceRootFolders, WorkerHubMethods.InspectWorkspaceFolder];

    /// <summary>Short reads that someone is waiting for get their own dedicated pool, separate from
    /// <see cref="ReadOnlyCommands"/>, so they never queue behind a workspace status rescan (which can fan out many
    /// GetGitChangeStatus calls) or any other command: opening a diff, and the two file reads of the version-file
    /// check, which the App waits for each time it applies hook syncs. Those two take milliseconds, but waited up
    /// to 7.5 s on the main pool behind a GitVersion run and up to 45 s on the read pool behind status scans and
    /// worktree inspections of 16 to 26 s each (logs of 2026-11-25, rounds 2 and 3).</summary>
    private static readonly HashSet<string> DiffCommands =
        ["GetGitFileDiff", WorkerHubMethods.CheckFileVersions, WorkerHubMethods.ResolveGeneratedPackageReferences];

    /// <summary>AI commands: bounded read-only Git evidence and one provider CLI process per generation. They get
    /// their own pool so a long CLI run never holds a main-pool slot (Sync, push, Update, hook syncs) or a read-pool
    /// slot (status scans).</summary>
    private static readonly HashSet<string> AiCommands =
        [WorkerHubMethods.CollectGitEvidence, WorkerHubMethods.GenerateAiText, WorkerHubMethods.GetAiProviders];

    public static WorkerCommandPool Resolve(string command)
    {
        if (DiffCommands.Contains(command))
            return WorkerCommandPool.Diff;
        if (ReadOnlyCommands.Contains(command))
            return WorkerCommandPool.Read;
        if (AiCommands.Contains(command))
            return WorkerCommandPool.Ai;
        return WorkerCommandPool.Main;
    }
}
