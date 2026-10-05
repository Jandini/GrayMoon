using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetWorkspaceExistsResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
}
