using System.Text.Json.Serialization;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class CreateGitWorktreeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Canonical worktree path as reported by Git after creation (or existing match).</summary>
    [JsonPropertyName("worktreePath")]
    public string? WorktreePath { get; set; }

    [JsonPropertyName("headSha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    /// <summary>True when the expected worktree already existed and was adopted without mutation.</summary>
    [JsonPropertyName("alreadyExisted")]
    public bool AlreadyExisted { get; set; }

    [JsonPropertyName("worktree")]
    public GitWorktreeInfo? Worktree { get; set; }
}
