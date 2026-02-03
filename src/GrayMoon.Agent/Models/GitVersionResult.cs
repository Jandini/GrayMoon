using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Models;

public sealed class GitVersionResult
{
    [JsonPropertyName("SemVer")]
    public string? SemVer { get; set; }

    [JsonPropertyName("FullSemVer")]
    public string? FullSemVer { get; set; }

    [JsonPropertyName("BranchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("EscapedBranchName")]
    public string? EscapedBranchName { get; set; }
}
