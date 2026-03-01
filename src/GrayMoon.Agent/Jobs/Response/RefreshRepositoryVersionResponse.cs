using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class RefreshRepositoryVersionResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("gitVersionError")]
    public string? GitVersionError { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }

    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }
}
