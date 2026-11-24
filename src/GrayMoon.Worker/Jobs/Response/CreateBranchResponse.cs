using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class CreateBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    // Populated only when SkipHooks=true (inline sync path)
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("fetchError")]
    public string? FetchError { get; set; }

    /// <summary>True when a local branch of that name was already there: it was checked out as it is and the requested base was not used.</summary>
    [JsonPropertyName("alreadyExisted")]
    public bool AlreadyExisted { get; set; }

    /// <summary>The ref the branch was created from (<c>origin/release/2.4</c> or <c>release/2.4</c>); null when the branch already existed or the repository has no commits.</summary>
    [JsonPropertyName("resolvedStartRef")]
    public string? ResolvedStartRef { get; set; }

    /// <summary>The commit the new branch points at; null when the branch already existed or the repository has no commits.</summary>
    [JsonPropertyName("baseCommitSha")]
    public string? BaseCommitSha { get; set; }

    /// <summary>The commit checked out after the command, also when the branch already existed; null when the repository has no commits.</summary>
    [JsonPropertyName("headCommitSha")]
    public string? HeadCommitSha { get; set; }
}
