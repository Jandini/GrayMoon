using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class SyncRepositoryDependenciesRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("projectUpdates")]
    public IReadOnlyList<SyncRepositoryDependenciesProjectUpdate>? ProjectUpdates { get; set; }
}

public sealed class SyncRepositoryDependenciesProjectUpdate
{
    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; set; }

    [JsonPropertyName("packageUpdates")]
    public IReadOnlyList<PackageVersionUpdate>? PackageUpdates { get; set; }
}

public sealed class PackageVersionUpdate
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }

    [JsonPropertyName("newVersion")]
    public string? NewVersion { get; set; }
}
