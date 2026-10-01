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

    /// <summary>
    /// Optional: this Feature's root folder (<c>featureStorageRoot\&lt;FeatureName&gt;</c>). Deleted after the
    /// worktree is removed, but only when it is then empty. Ignored unless <see cref="FeatureStorageRoot"/>
    /// is also set.
    /// </summary>
    [JsonPropertyName("featureRootPath")]
    public string? FeatureRootPath { get; set; }

    /// <summary>
    /// Optional: the Workspace's persisted Feature storage root, ending in <c>&lt;Workspace&gt;\features</c>.
    /// Required, together with <see cref="FeatureRootPath"/>, before any leftover worktree files are deleted;
    /// without it, leftover files are only reported. An old App that does not send this gets today's
    /// behaviour: no residue deletion.
    /// </summary>
    [JsonPropertyName("featureStorageRoot")]
    public string? FeatureStorageRoot { get; set; }
}
