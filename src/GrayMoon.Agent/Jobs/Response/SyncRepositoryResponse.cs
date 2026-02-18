using System.Text.Json.Serialization;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class SyncRepositoryResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("projects")]
    public IReadOnlyList<CsProjFileInfo>? Projects { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("localBranches")]
    public IReadOnlyList<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public IReadOnlyList<string>? RemoteBranches { get; set; }
}
