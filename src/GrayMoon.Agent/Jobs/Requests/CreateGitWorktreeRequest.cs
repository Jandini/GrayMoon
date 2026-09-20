using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

/// <summary>
/// Creates a linked Feature worktree from a committed base SHA:
/// <c>git worktree add -b &lt;branch&gt; &lt;worktreePath&gt; &lt;baseCommitSha&gt;</c>.
/// Offline-safe; never uses <c>--force</c>.
/// </summary>
public sealed class CreateGitWorktreeRequest
{
    [JsonPropertyName("mainRepositoryPath")]
    public string? MainRepositoryPath { get; set; }

    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    /// <summary>Feature branch name (equals Feature name).</summary>
    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    /// <summary>Committed HEAD SHA from the special Workspace checkout used as the start point.</summary>
    [JsonPropertyName("baseCommitSha")]
    public string? BaseCommitSha { get; set; }
}
