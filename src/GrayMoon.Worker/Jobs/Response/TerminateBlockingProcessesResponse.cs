using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class TerminateBlockingProcessesResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>One entry per selected process: what happened to it.</summary>
    [JsonPropertyName("outcomes")]
    public List<TerminateProcessOutcomeResponse>? Outcomes { get; set; }

    /// <summary>A fresh inspection of every requested path after the kills, in request order.</summary>
    [JsonPropertyName("results")]
    public List<InspectPathLocksResult>? Results { get; set; }
}

public sealed class TerminateProcessOutcomeResponse
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    /// <summary>A <see cref="Models.TerminateProcessOutcome"/> value.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }
}
