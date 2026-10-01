using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

/// <summary>
/// Live facts about one worktree, checked on the developer's machine. New fields added later must be
/// nullable with "unknown" as their null meaning, so an older Worker's response shape still deserializes.
/// </summary>
public sealed class InspectWorktreeResponse
{
    /// <summary>True when the worktree path appears in <c>git worktree list --porcelain</c> for the main repository.</summary>
    [JsonPropertyName("isRegistered")]
    public bool IsRegistered { get; set; }

    /// <summary>True when the worktree folder exists on disk.</summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>True when the worktree is locked (<c>git worktree lock</c>).</summary>
    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    /// <summary>Optional lock reason when <see cref="IsLocked"/> is true.</summary>
    [JsonPropertyName("lockReason")]
    public string? LockReason { get; set; }

    /// <summary>HEAD commit SHA, or null when it could not be determined.</summary>
    [JsonPropertyName("headSha")]
    public string? HeadSha { get; set; }

    /// <summary>Current branch short name, or null when HEAD is detached.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>True when <c>git status --porcelain=v1</c> reports any change (staged, unstaged, untracked, or conflicted).</summary>
    [JsonPropertyName("isDirty")]
    public bool? IsDirty { get; set; }

    /// <summary>Count of staged (index) changes.</summary>
    [JsonPropertyName("stagedCount")]
    public int? StagedCount { get; set; }

    /// <summary>Count of unstaged (working tree) changes.</summary>
    [JsonPropertyName("unstagedCount")]
    public int? UnstagedCount { get; set; }

    /// <summary>Count of untracked files.</summary>
    [JsonPropertyName("untrackedCount")]
    public int? UntrackedCount { get; set; }

    /// <summary>Count of unmerged (conflicted) paths.</summary>
    [JsonPropertyName("conflictCount")]
    public int? ConflictCount { get; set; }

    /// <summary>True when the current branch has a configured upstream.</summary>
    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>Commits on HEAD not on the upstream; null when there is no upstream.</summary>
    [JsonPropertyName("aheadOfUpstream")]
    public int? AheadOfUpstream { get; set; }

    /// <summary>Commits on the upstream not on HEAD; null when there is no upstream.</summary>
    [JsonPropertyName("behindUpstream")]
    public int? BehindUpstream { get; set; }

    /// <summary>Commits on HEAD not on <c>origin/&lt;defaultBranch&gt;</c>; null when that ref is missing.</summary>
    [JsonPropertyName("aheadOfDefault")]
    public int? AheadOfDefault { get; set; }

    /// <summary>Null on success; otherwise a short description of what could not be determined.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// True when <c>refs/heads/&lt;featureBranch&gt;</c> exists, from the request's optional
    /// <see cref="GrayMoon.Agent.Jobs.Requests.InspectWorktreeRequest.FeatureBranch"/>. Null when that
    /// request field was not set (09 SB-2, plan unit I1).
    /// </summary>
    [JsonPropertyName("featureBranchExists")]
    public bool? FeatureBranchExists { get; set; }

    /// <summary>HEAD commit SHA of the Feature branch; null when it does not exist or was not requested.</summary>
    [JsonPropertyName("featureBranchSha")]
    public string? FeatureBranchSha { get; set; }

    /// <summary>Commits on the Feature branch not on <c>origin/&lt;defaultBranch&gt;</c>; null when the Feature branch or that ref is missing, or it was not requested.</summary>
    [JsonPropertyName("featureBranchAheadOfDefault")]
    public int? FeatureBranchAheadOfDefault { get; set; }

    /// <summary>True when the Feature branch has a configured upstream; null when it does not exist or was not requested.</summary>
    [JsonPropertyName("featureBranchHasUpstream")]
    public bool? FeatureBranchHasUpstream { get; set; }

    /// <summary>Commits on the Feature branch not on its upstream; null when there is no upstream, the branch is missing, or it was not requested.</summary>
    [JsonPropertyName("featureBranchAheadOfUpstream")]
    public int? FeatureBranchAheadOfUpstream { get; set; }
}
