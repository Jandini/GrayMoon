using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Models;

public sealed class NotifyPayload
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    [JsonPropertyName("repositoryPath")]
    public string? RepositoryPath { get; set; }
}
