using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Computes the version the parent branch <see cref="ParentBranch"/> has at <see cref="CommitSha"/> (the branch point a
/// branch was created at), from whatever the repository has checked out, and whether that commit is on
/// <c>origin/&lt;parent&gt;</c>. Never fetches and never changes the checkout.
/// </summary>
public sealed class GetGitVersionAtBranchPointRequest : WorkspaceCommandRequest, IRepositoryNamedRequest, IWorkspaceNamedRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>The parent branch as it was requested, without <c>origin/</c>.</summary>
    [JsonPropertyName("parentBranch")]
    public string? ParentBranch { get; set; }

    /// <summary>The full SHA of the branch point.</summary>
    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }
}
