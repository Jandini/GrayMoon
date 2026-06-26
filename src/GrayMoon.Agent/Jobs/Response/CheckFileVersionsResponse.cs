using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class CheckFileVersionsResponse
{
    [JsonPropertyName("files")] public List<CheckFileVersionsResult>? Files { get; set; }
}

public sealed class CheckFileVersionsResult
{
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("totalMatchedLines")] public int TotalMatchedLines { get; set; }
    [JsonPropertyName("expectedTokenCount")] public int ExpectedTokenCount { get; set; }
    [JsonPropertyName("fileMissing")] public bool FileMissing { get; set; }
    [JsonPropertyName("outOfDateLines")] public List<CheckFileVersionsOutOfDateLine>? OutOfDateLines { get; set; }
    [JsonPropertyName("notMatchedTokens")] public List<string> NotMatchedTokens { get; set; } = [];
}

public sealed class CheckFileVersionsOutOfDateLine
{
    [JsonPropertyName("tokenName")] public string? TokenName { get; set; }
    [JsonPropertyName("currentValue")] public string? CurrentValue { get; set; }
    [JsonPropertyName("expectedValue")] public string? ExpectedValue { get; set; }
}
