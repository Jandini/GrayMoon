using System.Text.Json.Serialization;
namespace GrayMoon.Agent.Jobs.Requests;
public sealed class UpdateFileVersionsRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    /// <summary>File path relative to the repository root.</summary>
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    /// <summary>
    /// Multi-line pattern text. Each non-empty line must be in the form KEY={@reponame} or KEY={@reponame:branch|commit}.
    /// The prefix up to and including the token braces is matched against lines in the file; matching lines
    /// get their value replaced with the value resolved from <see cref="TokenValues"/>.
    /// </summary>
    [JsonPropertyName("versionPattern")] public string? VersionPattern { get; set; }
    /// <summary>Map of canonical token key (e.g. <c>@Repo</c>, <c>@Repo:commit</c>) to its resolved value string.</summary>
    [JsonPropertyName("tokenValues")] public Dictionary<string, string>? TokenValues { get; set; }
}
