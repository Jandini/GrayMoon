using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class RefreshBranchesResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("localBranches")]
    public IReadOnlyList<string> LocalBranches { get; set; } = Array.Empty<string>();

    [JsonPropertyName("remoteBranches")]
    public IReadOnlyList<string> RemoteBranches { get; set; } = Array.Empty<string>();

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Whether the checked-out branch has a configured upstream, read from git config rather than matched by name against the remote list.</summary>
    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>False when the upstream could not be determined, in which case <see cref="HasUpstream"/> must not overwrite persisted state.</summary>
    [JsonPropertyName("upstreamProbed")]
    public bool UpstreamProbed { get; set; }
}
