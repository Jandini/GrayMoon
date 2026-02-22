using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

public sealed class UpdateFileVersionsRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }

    /// <summary>File path relative to the repository root.</summary>
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }

    /// <summary>
    /// Multi-line pattern text. Each non-empty line must be in the form KEY={reponame}.
    /// The prefix up to and including '=' is matched against lines in the file; matching lines
    /// get their value replaced with the version resolved from <see cref="RepoVersions"/>.
    /// </summary>
    [JsonPropertyName("versionPattern")] public string? VersionPattern { get; set; }

    /// <summary>Map of repository name (as used in pattern tokens) to its current version string.</summary>
    [JsonPropertyName("repoVersions")] public Dictionary<string, string>? RepoVersions { get; set; }
}
