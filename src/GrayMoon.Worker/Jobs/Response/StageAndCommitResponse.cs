using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class StageAndCommitResponse : ICommandOutcome
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("committed")]
    public bool Committed { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
