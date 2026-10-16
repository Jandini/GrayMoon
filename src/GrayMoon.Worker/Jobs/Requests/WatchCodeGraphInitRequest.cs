using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>Streams the output of the CodeGraph index build running in a Feature root. Sent by Create / Repair Feature.</summary>
public sealed class WatchCodeGraphInitRequest
{
    /// <summary>The Feature root the build runs in.</summary>
    [JsonPropertyName("root")]
    public string? Root { get; set; }

    /// <summary>First output line to send: 0, then the previous response's <c>next</c>.</summary>
    [JsonPropertyName("from")]
    public int From { get; set; }
}
