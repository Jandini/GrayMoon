using System.Text.Json.Serialization;
using GrayMoon.Common.Ai;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Read-only Git evidence for one AI generation: every repository of one workspace commit or one shared pull request,
/// collected in one request so the App makes a single round trip.
/// </summary>
public sealed class CollectGitEvidenceRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("kind")]
    public GitEvidenceKind Kind { get; set; }

    /// <summary>Commit only: Staged (HEAD to index) or All (HEAD to working tree).</summary>
    [JsonPropertyName("commitMode")]
    public GitCommitEvidenceMode CommitMode { get; set; }

    [JsonPropertyName("repositories")]
    public List<GitEvidenceRepositoryScope>? Repositories { get; set; }

    [JsonPropertyName("limits")]
    public GitEvidenceLimits? Limits { get; set; }
}
