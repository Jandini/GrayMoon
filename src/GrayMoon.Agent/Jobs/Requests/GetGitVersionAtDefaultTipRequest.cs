using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

/// <summary>
/// Computes GitVersion for the tip of <c>origin/{defaultBranch}</c> without checking out that branch
/// (<c>dotnet-gitversion /c &lt;sha&gt; /nofetch</c>). Caller must have fetched recently.
/// </summary>
public sealed class GetGitVersionAtDefaultTipRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }
}
