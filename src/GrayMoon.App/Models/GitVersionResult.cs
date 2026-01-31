using System.Text.Json.Serialization;

namespace GrayMoon.App.Models;

public class GitVersionResult
{
    [JsonPropertyName("SemVer")]
    public string? SemVer { get; set; }

    [JsonPropertyName("FullSemVer")]
    public string? FullSemVer { get; set; }

    [JsonPropertyName("BranchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("EscapedBranchName")]
    public string? EscapedBranchName { get; set; }

    [JsonPropertyName("Major")]
    public int Major { get; set; }

    [JsonPropertyName("Minor")]
    public int Minor { get; set; }

    [JsonPropertyName("Patch")]
    public int Patch { get; set; }

    [JsonPropertyName("PreReleaseTag")]
    public string? PreReleaseTag { get; set; }

    [JsonPropertyName("Sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("ShortSha")]
    public string? ShortSha { get; set; }

    [JsonPropertyName("InformationalVersion")]
    public string? InformationalVersion { get; set; }
}
