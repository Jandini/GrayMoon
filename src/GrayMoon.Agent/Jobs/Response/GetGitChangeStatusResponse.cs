using System.Text.Json.Serialization;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetGitChangeStatusResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("snapshot")]
    public GitChangeSnapshot? Snapshot { get; set; }
}
