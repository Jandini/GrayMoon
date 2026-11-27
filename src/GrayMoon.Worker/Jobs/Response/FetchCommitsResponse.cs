using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class FetchCommitsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

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

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    /// <summary>
    /// The outgoing and incoming counts were computed (a failed git read leaves this false, and the App then keeps
    /// what it has stored). False on a tag or a detached checkout: there is no branch to count.
    /// </summary>
    [JsonPropertyName("commitCountsProbed")]
    public bool CommitCountsProbed { get; set; }

    [JsonPropertyName("localBranches")]
    public IReadOnlyList<string>? LocalBranches { get; set; }

    /// <summary>Origin branches after the pruning fetch, without the <c>origin/</c> prefix.</summary>
    [JsonPropertyName("remoteBranches")]
    public IReadOnlyList<string>? RemoteBranches { get; set; }

    /// <summary>The repository default branch without the <c>origin/</c> prefix, when known.</summary>
    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    /// <summary>The tag and branch lists are complete and may replace what the App has stored.</summary>
    [JsonPropertyName("branchesProbed")]
    public bool BranchesProbed { get; set; }
}
