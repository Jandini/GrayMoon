using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class EnsureGitChangesWatchersResponse
{
    /// <summary>Identifies this Worker process; a change between two calls means the Worker restarted and every watcher was lost.</summary>
    [JsonPropertyName("workerInstanceId")]
    public string WorkerInstanceId { get; set; } = string.Empty;

    /// <summary>How long a watcher survives without another renewal.</summary>
    [JsonPropertyName("effectiveGraceSeconds")]
    public int EffectiveGraceSeconds { get; set; }

    [JsonPropertyName("results")]
    public List<EnsureGitChangesWatchersResult> Results { get; set; } = [];
}

public sealed class EnsureGitChangesWatchersResult
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    /// <summary>Renewed, Created, Recovered, Degraded, UnderRemoval, Missing or Invalid.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
