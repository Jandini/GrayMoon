using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class RemoveGitWorktreeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>True when the path was already absent from Git's worktree list.</summary>
    [JsonPropertyName("alreadyRemoved")]
    public bool AlreadyRemoved { get; set; }
}
