using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Starts a CodeGraph index build in a Feature root. Sent at the end of Create Feature (and after a Feature is
/// repaired) when the Workspace definition turns CodeGraph on.
/// </summary>
public sealed class InitCodeGraphRequest
{
    /// <summary>The Feature root to index.</summary>
    [JsonPropertyName("targetRoot")]
    public string? TargetRoot { get; set; }
}
