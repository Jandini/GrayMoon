using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Reports everything removal needs to know about one worktree, checked live on the developer's
/// machine: registration, existence, lock state, dirty state, and commit counts vs upstream and
/// the default branch.
/// </summary>
public sealed class InspectWorktreeRequest
{
    /// <summary>Absolute path to the main repository checkout (or any worktree of that repo).</summary>
    [JsonPropertyName("mainRepositoryPath")]
    public string? MainRepositoryPath { get; set; }

    /// <summary>Absolute path to the worktree being inspected.</summary>
    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    /// <summary>Default branch short name (for example "main"), used to compute <c>aheadOfDefault</c>.</summary>
    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    /// <summary>Optional expected branch name; reserved for callers that want to compare against <c>branch</c> themselves.</summary>
    [JsonPropertyName("expectedBranch")]
    public string? ExpectedBranch { get; set; }

    /// <summary>
    /// Optional Feature branch name. When set, the response also reports that branch's own facts
    /// (<c>refs/heads/&lt;featureBranch&gt;</c>), computed from refs without checking anything out, so a
    /// caller can judge a Feature's own branch even when the worktree is currently on another branch or
    /// detached (09 SB-2, plan unit I1). Null (the default, and an older App's request shape) means no
    /// Feature-branch facts are computed and the response's featureBranch* fields are null.
    /// </summary>
    [JsonPropertyName("featureBranch")]
    public string? FeatureBranch { get; set; }
}
