using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

/// <summary>Lists linked worktrees for one repository via <c>git worktree list --porcelain</c>.</summary>
public sealed class ListGitWorktreesRequest
{
    /// <summary>Absolute path to the main repository checkout (or any worktree of that repo).</summary>
    [JsonPropertyName("mainRepositoryPath")]
    public string? MainRepositoryPath { get; set; }
}
