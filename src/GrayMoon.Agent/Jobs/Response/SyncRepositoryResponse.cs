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

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("gitVersionError")]
    public string? GitVersionError { get; set; }

    [JsonPropertyName("gitFetchError")]
    public string? GitFetchError { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }
}
