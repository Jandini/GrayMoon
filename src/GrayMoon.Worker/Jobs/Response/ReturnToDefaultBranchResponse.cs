using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class ReturnToDefaultBranchResponse
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

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("projects")]
    public IReadOnlyList<CsProjFileInfo>? Projects { get; set; }

    /// <summary>Authoritative post-checkout state with per-group probe markers. Null from workers that predate this field.</summary>
    [JsonPropertyName("state")]
    public RepositoryStateSnapshot? State { get; set; }

    /// <summary>Set when the remote branch delete was asked for and failed. The return itself carried on; the branch is still on the remote.</summary>
    [JsonPropertyName("remoteBranchDeleteError")]
    public string? RemoteBranchDeleteError { get; set; }

    /// <summary>Set when the local branch delete failed. The return itself carried on; the branch is still in the repository.</summary>
    [JsonPropertyName("localBranchDeleteError")]
    public string? LocalBranchDeleteError { get; set; }
}
