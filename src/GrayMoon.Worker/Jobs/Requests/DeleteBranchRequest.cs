using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class DeleteBranchRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("isRemote")]
    public bool IsRemote { get; set; }

    /// <summary>When true and deleting a local branch, uses git branch -D after -d failed (not fully merged).</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }

    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    /// <summary>
    /// When set for a remote delete, the Worker fetches then deletes with
    /// <c>--force-with-lease=refs/heads/&lt;branch&gt;:&lt;expectedSha&gt;</c> (D4). Null keeps the
    /// legacy <c>push origin --delete</c> path for callers that do not send a lease tip.
    /// </summary>
    [JsonPropertyName("expectedSha")]
    public string? ExpectedSha { get; set; }
}

