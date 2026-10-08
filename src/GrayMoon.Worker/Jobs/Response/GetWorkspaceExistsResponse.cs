using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetWorkspaceExistsResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>Whether the folder has no entries at all. Null when the folder does not exist.</summary>
    [JsonPropertyName("isEmpty")]
    public bool? IsEmpty { get; set; }
}
