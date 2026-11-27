namespace GrayMoon.Worker.Services;

/// <summary>Why a worktree removal did not finish, reported to the App as <c>failureKind</c>.</summary>
public enum WorktreeRemovalFailureKind
{
    /// <summary>No failure.</summary>
    None = 0,
    /// <summary>Git refused for a logical reason (not a worktree, the main worktree, validation failed, missing repository).</summary>
    GitRefusal = 1,
    /// <summary>The worktree has modified or untracked files and removal was not forced. Established by reading the worktree's status after git refused.</summary>
    UncommittedChanges = 2,
    /// <summary>The worktree is locked with <c>git worktree lock</c>. Established from the worktree inventory.</summary>
    WorktreeLocked = 3,
    /// <summary>A file or the folder is in use by another program (sharing violation, busy, cannot unlink).</summary>
    PathInUse = 4,
    /// <summary>The operating system denied access (often also an open file on Windows).</summary>
    AccessDenied = 5,
    /// <summary>Anything else, including an unexpected IO failure.</summary>
    Unknown = 6,
    /// <summary>A merge, rebase or cherry-pick is unfinished in the worktree; git would have removed it without asking.</summary>
    OperationInProgress = 7,
    /// <summary>The checked-out commit is on no branch or tag; removing the worktree would lose it.</summary>
    UnpreservedCommits = 8,
    /// <summary>The folder contains another Git repository, which the removal would delete with it.</summary>
    NestedRepository = 9,
    /// <summary>A forced removal found the worktree on another commit than the one whose changes were reviewed.</summary>
    ChangedSinceReview = 10
}

/// <summary>
/// The error codes <c>GitWorktreeService.RemoveWorktreeAsync</c> reports for a refusal it established from the
/// repository itself (the inventory, the worktree's status, its git directory), never from git's message text.
/// </summary>
public static class WorktreeRemovalErrorCodes
{
    public const string UncommittedChanges = "UncommittedChanges";
    public const string WorktreeLocked = "WorktreeLocked";
    public const string OperationInProgress = "OperationInProgress";
    public const string UnpreservedCommits = "UnpreservedCommits";
    public const string NestedRepository = "NestedRepository";
    public const string ChangedSinceReview = "ChangedSinceReview";
}

/// <summary>
/// Classification of a worktree removal error. The kinds that say user work is at stake (uncommitted changes, a
/// lock, an unfinished operation, unpreserved commits, a nested repository) come only from the Worker's own error
/// code, which is set from evidence. Git's message text only tells an open file from a denied access from a plain
/// refusal, which changes the wording of the report and nothing else: every one of them keeps the worktree.
/// </summary>
public static class WorktreeRemovalFailureClassifier
{
    private static readonly string[] LockedMarkers =
    [
        "cannot remove a locked working tree",
        "is locked",
    ];

    private static readonly string[] InUseMarkers =
    [
        "being used by another process",
        "sharing violation",
        "device or resource busy",
        "resource busy",
        "directory not empty",
        "unable to unlink",
        "unlink of file",
        "failed to delete",
        "could not be deleted",
        "could not be removed",
        "may still be open",
    ];

    private static readonly string[] AccessDeniedMarkers =
    [
        "permission denied",
        "access is denied",
        "access denied",
        "unauthorizedaccess",
    ];

    private static readonly string[] RefusalErrorCodes =
    [
        "RepositoryNotFound",
        "InvalidWorktreePath",
        "CannotRemovePrimary",
    ];

    public static WorktreeRemovalFailureKind Classify(string? errorCode, string? errorMessage)
    {
        switch (errorCode)
        {
            case WorktreeRemovalErrorCodes.UncommittedChanges:
                return WorktreeRemovalFailureKind.UncommittedChanges;
            case WorktreeRemovalErrorCodes.WorktreeLocked:
                return WorktreeRemovalFailureKind.WorktreeLocked;
            case WorktreeRemovalErrorCodes.OperationInProgress:
                return WorktreeRemovalFailureKind.OperationInProgress;
            case WorktreeRemovalErrorCodes.UnpreservedCommits:
                return WorktreeRemovalFailureKind.UnpreservedCommits;
            case WorktreeRemovalErrorCodes.NestedRepository:
                return WorktreeRemovalFailureKind.NestedRepository;
            case WorktreeRemovalErrorCodes.ChangedSinceReview:
                return WorktreeRemovalFailureKind.ChangedSinceReview;
        }

        if (!string.IsNullOrWhiteSpace(errorCode) && RefusalErrorCodes.Contains(errorCode, StringComparer.Ordinal))
            return WorktreeRemovalFailureKind.GitRefusal;

        if (string.IsNullOrWhiteSpace(errorMessage))
            return string.IsNullOrWhiteSpace(errorCode) ? WorktreeRemovalFailureKind.None : WorktreeRemovalFailureKind.Unknown;

        if (ContainsAny(errorMessage, LockedMarkers))
            return WorktreeRemovalFailureKind.WorktreeLocked;
        // "failed to delete '...': Permission denied" is how Git for Windows reports a file held open elsewhere, so the
        // in-use markers win over the plain access-denied ones.
        if (ContainsAny(errorMessage, InUseMarkers))
            return WorktreeRemovalFailureKind.PathInUse;
        if (ContainsAny(errorMessage, AccessDeniedMarkers))
            return WorktreeRemovalFailureKind.AccessDenied;
        if (errorMessage.Contains("fatal:", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("validation failed", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("is not a working tree", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("is a main working tree", StringComparison.OrdinalIgnoreCase))
        {
            return WorktreeRemovalFailureKind.GitRefusal;
        }

        return WorktreeRemovalFailureKind.Unknown;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
