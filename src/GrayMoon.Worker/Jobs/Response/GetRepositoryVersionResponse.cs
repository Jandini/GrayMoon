using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetRepositoryVersionResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>
    /// Whether a version provider actually ran. False means the workspace does not version its repositories,
    /// so an empty <see cref="Version"/> is not a failure. Null from a worker that predates workspace
    /// profiles, which always ran one, so the app keeps treating an empty version as a failure.
    /// </summary>
    [JsonPropertyName("versionProbed")]
    public bool? VersionProbed { get; set; }
}
