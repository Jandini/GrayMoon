using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetHostInfoResponse
{
    [JsonPropertyName("dotnetVersion")]
    public string? DotnetVersion { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("gitVersionToolVersion")]
    public string? GitVersionToolVersion { get; set; }

    /// <summary>Agent host user profile directory (e.g. C:\Users\name). Used to default Feature worktree storage.</summary>
    [JsonPropertyName("userProfilePath")]
    public string? UserProfilePath { get; set; }
}
