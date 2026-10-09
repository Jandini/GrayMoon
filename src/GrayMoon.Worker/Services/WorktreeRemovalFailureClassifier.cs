namespace GrayMoon.Worker.Services;

/// <summary>Why a worktree removal did not finish, reported to the App as <c>failureKind</c>.</summary>
public enum WorktreeRemovalFailureKind
{
    /// <summary>No failure.</summary>
    None = 0,
    /// <summary>Git refused for a logical reason (not a worktree, the main worktree, validation failed, missing repository).</summary>
    GitRefusal = 1,
    /// <summary>The worktree has modified or untracked files and removal was not forced.</summary>
    UncommittedChanges = 2,
    /// <summary>The worktree is locked with <c>git worktree lock</c>.</summary>
    WorktreeLocked = 3,
    /// <summary>A file or the folder is in use by another program (sharing violation, busy, cannot unlink).</summary>
    PathInUse = 4,
    /// <summary>The operating system denied access (often also an open file on Windows).</summary>
    AccessDenied = 5,
    /// <summary>Anything else, including an unexpected IO failure.</summary>
    Unknown = 6
}

/// <summary>Pure classification of a worktree removal error, from the Worker's error code and Git's message.</summary>
public static class WorktreeRemovalFailureClassifier
{
    private static readonly string[] LockedMarkers =
    [
        "cannot remove a locked working tree",
        "is locked",
    ];

    private static readonly string[] UncommittedMarkers =
    [
        "contains modified or untracked files",
        "use --force to delete it",
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
        if (!string.IsNullOrWhiteSpace(errorCode) && RefusalErrorCodes.Contains(errorCode, StringComparer.Ordinal))
            return WorktreeRemovalFailureKind.GitRefusal;

        if (string.IsNullOrWhiteSpace(errorMessage))
            return string.IsNullOrWhiteSpace(errorCode) ? WorktreeRemovalFailureKind.None : WorktreeRemovalFailureKind.Unknown;

        if (ContainsAny(errorMessage, LockedMarkers))
            return WorktreeRemovalFailureKind.WorktreeLocked;
        if (ContainsAny(errorMessage, UncommittedMarkers))
            return WorktreeRemovalFailureKind.UncommittedChanges;
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
