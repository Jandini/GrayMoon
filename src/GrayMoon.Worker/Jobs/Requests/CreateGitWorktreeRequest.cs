using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Creates a linked Feature worktree from a committed base SHA:
/// <c>git worktree add -b &lt;branch&gt; &lt;worktreePath&gt; &lt;baseCommitSha&gt;</c>, or
/// <c>git worktree add --detach &lt;worktreePath&gt; &lt;baseCommitSha&gt;</c> when <see cref="Detach"/> is set.
/// Offline-safe; never uses <c>--force</c>.
/// </summary>
public sealed class CreateGitWorktreeRequest
{
    [JsonPropertyName("mainRepositoryPath")]
    public string? MainRepositoryPath { get; set; }

    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    /// <summary>Feature branch name (equals Feature name). Ignored when <see cref="Detach"/> is set.</summary>
    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    /// <summary>
    /// Create a detached worktree at <see cref="BaseCommitSha"/> with no Feature branch
    /// (repositories pinned to a tag in the special Workspace stay on that tag).
    /// </summary>
    [JsonPropertyName("detach")]
    public bool Detach { get; set; }

    /// <summary>When set with <see cref="RepositoryId"/>, refreshes the shared sync hooks before creating the worktree.</summary>
    [JsonPropertyName("workspaceId")]
    public int? WorkspaceId { get; set; }

    [JsonPropertyName("repositoryId")]
    public int? RepositoryId { get; set; }

    /// <summary>Committed HEAD SHA from the special Workspace checkout used as the start point.</summary>
    [JsonPropertyName("baseCommitSha")]
    public string? BaseCommitSha { get; set; }

    /// <summary>
    /// Feature parent branch for ahead/behind divergence (PR base). Persisted on the new worktree's git dir
    /// so commit/checkout hooks count vs parent instead of the repository default.
    /// </summary>
    [JsonPropertyName("divergenceBaseBranch")]
    public string? DivergenceBaseBranch { get; set; }
}
