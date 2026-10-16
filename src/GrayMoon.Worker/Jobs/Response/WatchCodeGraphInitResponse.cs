using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class WatchCodeGraphInitResponse
{
    /// <summary>
    /// <c>Running</c> (watch again from <see cref="Next"/>), <c>Completed</c>, <c>Failed</c>, <c>Stopped</c> or
    /// <c>NotRunning</c> (no build was started there).
    /// </summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>The output line the next watch starts from.</summary>
    [JsonPropertyName("next")]
    public int Next { get; set; }
}
