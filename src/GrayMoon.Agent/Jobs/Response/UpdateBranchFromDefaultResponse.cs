using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class UpdateBranchFromDefaultResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("hasConflicts")]
    public bool HasConflicts { get; set; }

    [JsonPropertyName("conflictFiles")]
    public IReadOnlyList<string>? ConflictFiles { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }
}
