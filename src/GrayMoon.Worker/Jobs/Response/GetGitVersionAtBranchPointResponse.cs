using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetGitVersionAtBranchPointResponse
{
    /// <summary>True when both checks ran: the ancestry check answered and GitVersion versioned the parent branch at the commit.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>The parent's InformationalVersion at the commit; set only when <see cref="Success"/> is true.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>True when the commit is reachable from <c>origin/&lt;parent&gt;</c>; null when that could not be decided.</summary>
    [JsonPropertyName("baselineOnRemoteParent")]
    public bool? BaselineOnRemoteParent { get; set; }
}
