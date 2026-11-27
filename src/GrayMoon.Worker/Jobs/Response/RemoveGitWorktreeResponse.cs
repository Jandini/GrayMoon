using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class RemoveGitWorktreeResponse : ICommandOutcome
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

    /// <summary>The branch the removed worktree was on, from the inventory read before the removal; null when detached or when it was not registered.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>The commit the removed worktree was on; null when it was not registered.</summary>
    [JsonPropertyName("headSha")]
    public string? HeadSha { get; set; }

    /// <summary>Whether the removed worktree was on a detached HEAD; null when it was not registered.</summary>
    [JsonPropertyName("wasDetached")]
    public bool? WasDetached { get; set; }
}
