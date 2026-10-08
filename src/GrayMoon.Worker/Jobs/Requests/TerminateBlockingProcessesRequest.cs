using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Ends processes the user selected in the Remove Feature dialog. The Worker inspects <see cref="Paths"/> again and ends only a
/// selected process that still holds one of them now, still has the same start time, and is not protected.
/// </summary>
public sealed class TerminateBlockingProcessesRequest
{
    /// <summary>Absolute Feature folder paths (resolved by the App from its own database).</summary>
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }

    /// <summary>The processes the user selected, each with the start time the inspection reported for it.</summary>
    [JsonPropertyName("processes")]
    public List<TerminateProcessSelection>? Processes { get; set; }
}

public sealed class TerminateProcessSelection
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("startTimeUtc")]
    public DateTime? StartTimeUtc { get; set; }
}
