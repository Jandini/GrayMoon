using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Response from POST /api/commitsync. Agent may send PascalCase; use case-insensitive deserialization.</summary>
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

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
