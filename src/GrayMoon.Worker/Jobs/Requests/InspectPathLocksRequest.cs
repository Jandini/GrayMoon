using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>Finds the processes that keep each of <see cref="Paths"/> in use. Read-only; never closes a process.</summary>
public sealed class InspectPathLocksRequest
{
    /// <summary>Absolute file or folder paths to inspect.</summary>
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }
}
