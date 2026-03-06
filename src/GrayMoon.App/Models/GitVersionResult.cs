using System.Text.Json.Serialization;

namespace GrayMoon.App.Models;

public class GitVersionResult
{
    [JsonPropertyName("InformationalVersion")]
    public string? InformationalVersion { get; set; }

    [JsonPropertyName("BranchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("EscapedBranchName")]
    public string? EscapedBranchName { get; set; }
}
