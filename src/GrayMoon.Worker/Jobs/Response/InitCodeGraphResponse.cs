using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class InitCodeGraphResponse
{
    /// <summary>
    /// <c>Started</c>, <c>AlreadyInitialized</c>, <c>AlreadyRunning</c>, <c>NotInstalled</c>,
    /// <c>SourceNotInitialized</c> or <c>TargetMissing</c>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }
}
