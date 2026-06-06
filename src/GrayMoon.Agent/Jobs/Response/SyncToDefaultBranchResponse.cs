using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class SyncToDefaultBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("localBranches")]
    public IReadOnlyList<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public IReadOnlyList<string>? RemoteBranches { get; set; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }
}
