using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class SyncRepositoryDependenciesResponse
{
    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }
}
