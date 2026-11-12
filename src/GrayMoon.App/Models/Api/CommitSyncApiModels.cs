using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Notifications;

namespace GrayMoon.App.Models.Api;

/// <summary>Response from POST /api/commitsync. Worker may send PascalCase; use case-insensitive deserialization.</summary>
public sealed class CommitSyncResponse
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

    /// <summary>Pull-only: git refused the fast-forward because local changes would be overwritten. Nothing was changed.</summary>
    [JsonPropertyName("localChangesBlockPull")]
    public bool LocalChangesBlockPull { get; set; }

    /// <summary>Pull-only: the branch has commits that are not on its remote branch, so nothing was pulled.</summary>
    [JsonPropertyName("hasLocalCommits")]
    public bool HasLocalCommits { get; set; }

    /// <summary>Full sha of HEAD after the command. Null from workers that predate it or when it could not be read.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>Full post-pull state. Null from workers that predate it, in which case only the flat count fields are usable.</summary>
    [JsonPropertyName("state")]
    public RepositoryStateSnapshot? State { get; set; }
}
