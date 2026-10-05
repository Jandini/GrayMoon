using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetGitVersionAtDefaultTipResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
