using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

/// <summary>
/// Removes a linked worktree via <c>git worktree remove</c>.
/// <see cref="Force"/> may be true only when the caller has already authorized destructive discard.
/// </summary>
public sealed class RemoveGitWorktreeRequest
{
    [JsonPropertyName("mainRepositoryPath")]
    public string? MainRepositoryPath { get; set; }

    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    /// <summary>When true, runs <c>git worktree remove --force</c>. Must only be set after explicit authorization.</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }
}
