using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

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
}
