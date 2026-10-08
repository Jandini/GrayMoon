using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class InspectPathLocksResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>One entry per requested path, in request order.</summary>
    [JsonPropertyName("results")]
    public List<InspectPathLocksResult>? Results { get; set; }
}

public sealed class InspectPathLocksResult
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>False when the path no longer exists (nothing left to block).</summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("blockingProcesses")]
    public List<BlockingProcessResponse>? BlockingProcesses { get; set; }

    [JsonPropertyName("mayBeIncomplete")]
    public bool MayBeIncomplete { get; set; }

    [JsonPropertyName("diagnostic")]
    public string? Diagnostic { get; set; }
}
