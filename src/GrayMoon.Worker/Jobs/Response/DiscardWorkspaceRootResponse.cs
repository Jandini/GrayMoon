using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class DiscardWorkspaceRootResponse
{
    /// <summary>True when nothing the restore created is left behind.</summary>
    [JsonPropertyName("removed")]
    public bool Removed { get; set; }

    /// <summary>Why the folder (or part of it) was left in place. Null when <see cref="Removed"/> is true.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
