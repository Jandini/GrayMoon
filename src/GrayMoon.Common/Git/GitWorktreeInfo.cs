using System.Text.Json.Serialization;

namespace GrayMoon.Common.Git;

/// <summary>
/// One entry from <c>git worktree list --porcelain</c>.
/// </summary>
public sealed class GitWorktreeInfo
{
    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    [JsonPropertyName("headSha")]
    public string? HeadSha { get; set; }

    /// <summary>Full ref when present (e.g. <c>refs/heads/main</c>).</summary>
    [JsonPropertyName("branchRef")]
    public string? BranchRef { get; set; }

    /// <summary>Short branch name derived from <see cref="BranchRef"/> when it is a heads ref.</summary>
    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("isDetached")]
    public bool IsDetached { get; set; }

    [JsonPropertyName("isBare")]
    public bool IsBare { get; set; }

    [JsonPropertyName("isPrunable")]
    public bool IsPrunable { get; set; }

    /// <summary>Optional prune reason from porcelain when <see cref="IsPrunable"/> is true.</summary>
    [JsonPropertyName("prunableReason")]
    public string? PrunableReason { get; set; }
}
