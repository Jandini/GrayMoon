using System.Text.Json.Serialization;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetGitFileDiffResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("diff")]
    public GitDiffDocument? Diff { get; set; }
}
