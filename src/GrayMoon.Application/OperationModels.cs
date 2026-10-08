namespace GrayMoon.Application;

public sealed record OperationProgress(string Message, int? Completed = null, int? Total = null);

public sealed record OperationResult(
    bool Success,
    string? Error = null,
    IReadOnlyDictionary<int, string>? RepoErrors = null,
    IReadOnlyDictionary<int, string>? LevelErrors = null)
{
    /// <summary>
    /// Per-repository outcome of a successful Remove Feature (D2): worktree removed, local branch
    /// outcome and leftover-files report. Null for every other operation that returns this type, and
    /// for a Remove Feature that did not succeed (the error path already reports via <see cref="Error"/>).
    /// </summary>
    public IReadOnlyList<RemoveFeatureRepositoryReport>? RemoveFeatureReport { get; init; }

    /// <summary>
    /// For a Remove Feature that failed because a worktree folder is in use (or access was denied): the processes the
    /// Worker found holding each such folder. Null for every other operation and failure.
    /// </summary>
    public IReadOnlyList<RemoveFeatureRepositoryBlockers>? RemoveFeatureBlockers { get; init; }

    public static OperationResult Ok(
        IReadOnlyDictionary<int, string>? repoErrors = null,
        IReadOnlyDictionary<int, string>? levelErrors = null)
        => new(true, null, repoErrors, levelErrors);

    public static OperationResult Fail(
        string error,
        IReadOnlyDictionary<int, string>? repoErrors = null,
        IReadOnlyDictionary<int, string>? levelErrors = null)
        => new(false, error, repoErrors, levelErrors);
}

/// <summary>Outcome of the local Feature branch delete for one repository, in a Remove Feature report (D2).</summary>
public enum RemoveFeatureBranchOutcome
{
    /// <summary>Tag-pinned repository; this Feature never had a branch to delete.</summary>
    NotApplicable = 0,
    Deleted = 1,
    /// <summary>Delete was refused because the branch has commits not on the default branch and force was not authorized.</summary>
    KeptUnmerged = 2,
    Failed = 3,
    /// <summary>User left "Delete local Feature branches" unticked (D4).</summary>
    Kept = 4
}

/// <summary>Outcome of the remote Feature branch delete for one repository, in a Remove Feature report (D4).</summary>
public enum RemoveFeatureRemoteBranchOutcome
{
    /// <summary>No remote Feature branch, or remote delete was not requested for this repository.</summary>
    NotApplicable = 0,
    Deleted = 1,
    /// <summary>User left "Delete remote Feature branches" unticked.</summary>
    Kept = 2,
    /// <summary>Remote tip no longer matched the Feature's local tip (lease refused).</summary>
    RefusedLease = 3,
    Failed = 4
}

/// <summary>
/// One repository's outcome from a successful Remove Feature, for the report shown to the user (D2).
/// </summary>
public sealed record RemoveFeatureRepositoryReport(
    int WorkspaceRepositoryId,
    string RepositoryName,
    bool WorktreeRemoved,
    RemoveFeatureBranchOutcome BranchOutcome,
    string? BranchMessage,
    bool ResidueRemaining,
    int ResidueFileCount,
    IReadOnlyList<string>? ResidueSampleFiles,
    string? ResidueMessage,
    /// <summary>
    /// When the worktree was not on its Feature branch at remove time (09 SB-2), the branch (or
    /// "(detached commit)") that was actually kept; the Feature branch named by
    /// <see cref="BranchOutcome"/> is what was deleted. Null when there was no drift.
    /// </summary>
    string? KeptBranchName = null,
    RemoveFeatureRemoteBranchOutcome RemoteBranchOutcome = RemoveFeatureRemoteBranchOutcome.NotApplicable,
    string? RemoteBranchMessage = null)
{
    /// <summary>Processes keeping the leftover folder in use, when the Worker looked them up; null when it did not (old Worker, no residue).</summary>
    public IReadOnlyList<RemoveFeatureBlockingProcess>? BlockingProcesses { get; init; }

    /// <summary>True when <see cref="BlockingProcesses"/> may miss some blockers.</summary>
    public bool BlockersMayBeIncomplete { get; init; }

    /// <summary>Short explanation for an incomplete blocker lookup; null when there is nothing to add.</summary>
    public string? BlockersDiagnostic { get; init; }

    /// <summary>
    /// What "Retry" needs to delete the leftover files again once the blockers are closed; null when a retry cannot help
    /// (no residue, or the Workspace-role root worktree, whose leftovers GrayMoon never deletes itself).
    /// </summary>
    public RemoveFeatureResidueTarget? ResidueTarget { get; init; }
}

/// <summary>
/// One local process keeping a Feature worktree folder in use (Remove Feature diagnostics). Local only: never persisted
/// or sent to a connector. <see cref="Kind"/> and <see cref="Reason"/> are the Worker's names
/// (Kind: Unknown, Application, Service, Explorer, Console, Critical; Reason: OpenFile, WorkingDirectory).
/// </summary>
public sealed record RemoveFeatureBlockingProcess(
    int ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    string? ServiceName,
    string? Kind,
    string? Reason)
{
    public const string WorkingDirectoryReason = "WorkingDirectory";

    /// <summary>True when the process's current folder is inside the worktree (a shell or an AI tool left there).</summary>
    public bool IsWorkingDirectory => string.Equals(Reason, WorkingDirectoryReason, StringComparison.Ordinal);
}

/// <summary>The blockers found for one repository's worktree folder.</summary>
public sealed record RemoveFeatureRepositoryBlockers(
    int WorkspaceRepositoryId,
    string RepositoryName,
    string WorktreePath,
    IReadOnlyList<RemoveFeatureBlockingProcess> Processes,
    bool MayBeIncomplete,
    string? Diagnostic)
{
    /// <summary>False once the folder no longer exists (after a refresh): nothing is left to block.</summary>
    public bool PathExists { get; init; } = true;
}

/// <summary>
/// Paths needed to retry the Worker's guarded leftover-file cleanup for one repository of an already removed Feature.
/// Built by the App from its own database at remove time; never from user input.
/// </summary>
public sealed record RemoveFeatureResidueTarget(
    string MainRepositoryPath,
    string WorktreePath,
    string? FeatureRootPath,
    string? FeatureStorageRoot);

/// <summary>
/// Result of a dependency-update run. <see cref="Success"/> is false when any repo or workspace-level
/// error was reported; chained push (Prepare Workspace / Update and Push) must not run in that case.
/// An empty <see cref="SyncedRepoIds"/> with <see cref="Success"/> true means nothing needed updating.
/// </summary>
public sealed record DependencyUpdateRunResult(bool Success, IReadOnlySet<int> SyncedRepoIds)
{
    public static DependencyUpdateRunResult Ok(IReadOnlySet<int>? syncedRepoIds = null)
        => new(true, syncedRepoIds ?? new HashSet<int>());

    public static DependencyUpdateRunResult Failed()
        => new(false, new HashSet<int>());

    public bool ShouldChainPush(bool pushRequested) => pushRequested && Success;
}

public static class OperationProgressExtensions
{
    public static void Report(
        this IProgress<OperationProgress>? progress,
        string message,
        int? completed = null,
        int? total = null)
        => progress?.Report(new OperationProgress(message, completed, total));

    public static Action<string> ToMessageAction(this IProgress<OperationProgress>? progress)
        => message => progress.Report(message);

    public static IProgress<OperationProgress> ToOperationProgress(this Action<string> report)
        => new Progress<OperationProgress>(p => report(p.Message));

    public static void ShowRepoErrors(this OperationResult result, Action<string> showError)
    {
        if (result.RepoErrors is { Count: > 0 })
        {
            foreach (var (id, err) in result.RepoErrors)
                showError($"{id}: {err}");
            return;
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
            showError(result.Error);
    }
}
