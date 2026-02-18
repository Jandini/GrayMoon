using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetBranchesResponse
{
    [JsonPropertyName("localBranches")]
    public IReadOnlyList<string> LocalBranches { get; set; } = Array.Empty<string>();

    [JsonPropertyName("remoteBranches")]
    public IReadOnlyList<string> RemoteBranches { get; set; } = Array.Empty<string>();

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }
}
