namespace GrayMoon.Abstractions.Worker;

/// <summary>
/// SignalR hub method names used between the app and the worker. Use these constants to avoid typos and drift.
/// </summary>
public static class WorkerHubMethods
{
    /// <summary>App → Worker: send a command (requestId, command, args).</summary>
    public const string RequestCommand = "RequestCommand";

    /// <summary>App → Worker: cancel a previously sent command (requestId). Used when the App aborts a background job.</summary>
    public const string CancelCommand = "CancelCommand";

    /// <summary>Worker → App: command completed (requestId, payload).</summary>
    public const string ResponseCommand = "ResponseCommand";

    /// <summary>Worker → App: streaming command line / stdout / stderr (requestId, streamLabel, kind, text). streamLabel drives the UI prefix (repo, workspace, or command).</summary>
    public const string CommandOutput = "CommandOutput";

    /// <summary>Worker → App: repository sync result from hooks (notification).</summary>
    public const string SyncCommand = "SyncCommand";

    /// <summary>Worker → App: report worker SemVer on connect/reconnect.</summary>
    public const string ReportSemVer = "ReportSemVer";

    /// <summary>Worker → App: report worker job queue status (total pending, per-workspace counts).</summary>
    public const string ReportQueueStatus = "ReportQueueStatus";

    /// <summary>App → Worker: request worker self-update (InstallUrl in payload). Allowed even when the Worker version does not match the App, so the payload must stay unchanged.</summary>
    public const string SelfUpdate = "SelfUpdate";

    /// <summary>App → Worker: host diagnostics (Worker, dotnet, git and GitVersion versions) for the Worker page. Allowed even when the Worker version does not match the App.</summary>
    public const string GetHostInfo = "GetHostInfo";

    /// <summary>App → Worker: check configured version files and return per-line staleness (read-only, no file writes).</summary>
    public const string CheckFileVersions = "CheckFileVersions";

    /// <summary>App → Worker: resolve current HEAD commit SHAs for named repositories (read-only, <c>git rev-parse HEAD</c> only).</summary>
    public const string GetHeadCommits = "GetHeadCommits";

    /// <summary>App → Worker: for configured .csproj version files, resolve which PackageReference Include name each version-pattern line refers to (read-only, no file writes).</summary>
    public const string ResolveGeneratedPackageReferences = "ResolveGeneratedPackageReferences";

    /// <summary>Worker → App: an unsolicited Git Changes status snapshot for one repository (watcher-driven or post-mutation refresh).</summary>
    public const string GitChangesSnapshotUpdated = "GitChangesSnapshotUpdated";

    /// <summary>App → Worker: list linked worktrees for one repository (<c>git worktree list --porcelain</c>).</summary>
    public const string ListGitWorktrees = "ListGitWorktrees";

    /// <summary>App → Worker: create a Feature linked worktree from a committed HEAD SHA (offline-safe, no <c>--force</c>).</summary>
    public const string CreateGitWorktree = "CreateGitWorktree";

    /// <summary>App → Worker: remove a linked worktree; force only when the request explicitly authorizes it.</summary>
    public const string RemoveGitWorktree = "RemoveGitWorktree";

    /// <summary>App → Worker: report everything removal needs to know about one worktree, checked live (read-only).</summary>
    public const string InspectWorktree = "InspectWorktree";

    /// <summary>App → Worker: list the local processes keeping each given path in use (read-only lock diagnostics).</summary>
    public const string InspectPathLocks = "InspectPathLocks";

    /// <summary>App → Worker: attach a remote repository to the Workspace root as its working tree (clone into an empty root, or init + fetch + checkout in a non-empty one).</summary>
    public const string AttachWorkspaceRepository = "AttachWorkspaceRepository";

    /// <summary>App → Worker: roll back the Workspace root of a failed restore; deletes only an empty folder or a clean clone of the restored repository.</summary>
    public const string DiscardWorkspaceRoot = "DiscardWorkspaceRoot";

    /// <summary>App → Worker: write one text file inside a repository working tree (atomic, UTF-8 without BOM, optionally only when the content changed).</summary>
    public const string WriteRepositoryFile = "WriteRepositoryFile";
}
