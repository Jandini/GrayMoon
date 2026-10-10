using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Starts a CodeGraph index build in a new Feature root, only when the Workspace root already has an index.
/// Sent at the end of Create Feature (and after a Feature is repaired).
/// </summary>
public sealed class InitCodeGraphRequest
{
    /// <summary>The Workspace root whose <c>.codegraph</c> index decides whether the Feature gets one.</summary>
    [JsonPropertyName("sourceRoot")]
    public string? SourceRoot { get; set; }

    /// <summary>The Feature root to index.</summary>
    [JsonPropertyName("targetRoot")]
    public string? TargetRoot { get; set; }
}
