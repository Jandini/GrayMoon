using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Notifications;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class CommitSyncRepositoryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("mergeConflict")]
    public bool MergeConflict { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Full post-pull state, so the app replaces every count the pull changed instead of only the two it used to report.</summary>
    [JsonPropertyName("state")]
    public RepositoryStateSnapshot? State { get; set; }
}
