using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class RemoveGitWorktreeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>True when the path was already absent from Git's worktree list.</summary>
    [JsonPropertyName("alreadyRemoved")]
    public bool AlreadyRemoved { get; set; }

    /// <summary>True when files remain on disk after this call, whether deletion failed or a safety guard blocked it.</summary>
    [JsonPropertyName("residueRemaining")]
    public bool ResidueRemaining { get; set; }

    /// <summary>Count of files left behind; 0 when nothing is left.</summary>
    [JsonPropertyName("residueFileCount")]
    public int ResidueFileCount { get; set; }

    /// <summary>Up to 5 relative paths of files left behind, for the Remove report.</summary>
    [JsonPropertyName("residueSampleFiles")]
    public List<string>? ResidueSampleFiles { get; set; }

    /// <summary>Null when there is no residue; otherwise why cleanup did not finish (a safety guard, a locked file, or similar).</summary>
    [JsonPropertyName("residueMessage")]
    public string? ResidueMessage { get; set; }

    /// <summary>
    /// <c>WorktreeRemovalFailureKind</c> name when the removal failed (GitRefusal, UncommittedChanges, WorktreeLocked,
    /// PathInUse, AccessDenied or Unknown); null on success.
    /// </summary>
    [JsonPropertyName("failureKind")]
    public string? FailureKind { get; set; }

    /// <summary>
    /// Processes keeping the worktree folder in use. Only looked up when the removal failed because the folder is in use or
    /// access was denied, or when files were left behind; null when no lookup ran (a clean removal never pays for one).
    /// </summary>
    [JsonPropertyName("blockingProcesses")]
    public List<BlockingProcessResponse>? BlockingProcesses { get; set; }

    /// <summary>True when <see cref="BlockingProcesses"/> may miss some blockers (see the lock inspector's diagnostic).</summary>
    [JsonPropertyName("blockersMayBeIncomplete")]
    public bool BlockersMayBeIncomplete { get; set; }

    /// <summary>Short explanation for an incomplete or failed blocker lookup; null when there is nothing to add.</summary>
    [JsonPropertyName("blockersDiagnostic")]
    public string? BlockersDiagnostic { get; set; }
}
