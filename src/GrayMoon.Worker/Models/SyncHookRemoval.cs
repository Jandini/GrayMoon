namespace GrayMoon.Worker.Models;

/// <summary>What removing GrayMoon's sync hooks from one repository came to.</summary>
public enum SyncHookRemovalOutcome
{
    /// <summary>At least one GrayMoon hook file was deleted.</summary>
    Removed,

    /// <summary>The repository holds no GrayMoon hook.</summary>
    NothingToRemove,

    /// <summary>The folder does not exist, so there is nothing of GrayMoon's in it.</summary>
    NotFound,

    /// <summary>The folder has no <c>.git</c>, so it has no hooks folder.</summary>
    NotARepository,

    /// <summary>A hook file or the folder could not be handled. See <see cref="SyncHookRemovalResult.FailureKind"/>.</summary>
    Failed,
}

/// <summary>Why a removal failed. The App turns it into the message the user reads.</summary>
public enum SyncHookRemovalFailureKind
{
    None,

    /// <summary>Git could not say where the repository keeps its hooks.</summary>
    GitFailed,

    /// <summary>A GrayMoon hook file could not be deleted.</summary>
    HookNotDeleted,

    /// <summary>GrayMoon's hook is gone, but the user's own replaced hook could not be renamed back.</summary>
    HookNotRestored,

    /// <summary>The Worker could not let go of the folder (its own watchers or processes) in time.</summary>
    WorkerBusy,

    /// <summary>The repository name is not a folder name.</summary>
    InvalidName,
}

/// <summary>
/// Where the hooks of one repository are, resolved with git before any file is touched. <see cref="Settled"/> is set
/// when no file work is needed (the folder is missing, is not a repository, or git could not answer).
/// </summary>
public sealed record SyncHookRemovalTarget(
    string RepositoryPath,
    IReadOnlyList<string> HookDirectories,
    SyncHookRemovalResult? Settled);

public sealed record SyncHookRemovalResult
{
    public SyncHookRemovalOutcome Outcome { get; init; }

    public SyncHookRemovalFailureKind FailureKind { get; init; }

    /// <summary>The file or folder the failure is about.</summary>
    public string? FailedPath { get; init; }

    /// <summary>The system's or git's own message for the failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Names of the GrayMoon hook files that were deleted.</summary>
    public IReadOnlyList<string> RemovedHooks { get; init; } = [];

    /// <summary>Hook names whose own, earlier replaced file was renamed back.</summary>
    public IReadOnlyList<string> RestoredHooks { get; init; } = [];

    /// <summary>Hook files that are not GrayMoon's and were left as they are.</summary>
    public IReadOnlyList<string> ForeignHooks { get; init; } = [];

    /// <summary>Older replaced hook files that were left in place because a newer one was renamed back.</summary>
    public IReadOnlyList<string> LeftoverReplacedHooks { get; init; } = [];

    public bool Succeeded => Outcome != SyncHookRemovalOutcome.Failed;

    public static SyncHookRemovalResult Of(SyncHookRemovalOutcome outcome) => new() { Outcome = outcome };

    public static SyncHookRemovalResult Failure(SyncHookRemovalFailureKind kind, string? path, string? message) =>
        new() { Outcome = SyncHookRemovalOutcome.Failed, FailureKind = kind, FailedPath = path, ErrorMessage = message };
}
