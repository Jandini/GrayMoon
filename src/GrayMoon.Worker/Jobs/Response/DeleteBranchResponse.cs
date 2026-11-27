using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class DeleteBranchResponse : ICommandOutcome
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Why the branch was not deleted: NotFound, CheckedOut, NotFullyMerged, TipChanged, LeaseRefused, Protected
    /// or Failed (<c>BranchDeleteFailureKinds</c>). Null on success.
    /// </summary>
    [JsonPropertyName("failureKind")]
    public string? FailureKind { get; set; }

    /// <summary>For a local delete: the branch tip read before the delete, also when the branch was kept. Null when the branch was not found.</summary>
    [JsonPropertyName("branchSha")]
    public string? BranchSha { get; set; }

    /// <summary>For a local delete: whether the branch had a configured upstream. Null when the branch was not found.</summary>
    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>For a remote delete: the branch was already gone on the remote, so nothing was deleted now.</summary>
    [JsonPropertyName("alreadyAbsent")]
    public bool AlreadyAbsent { get; set; }
}
