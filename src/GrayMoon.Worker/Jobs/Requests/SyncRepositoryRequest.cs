using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

public sealed class SyncRepositoryRequest : WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("cloneUrl")]
    public string? CloneUrl { get; set; }

    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; set; }

    /// <summary>
    /// Optional branch name for ahead/behind divergence (Feature parent / PR base).
    /// When set, counts are vs <c>origin/&lt;name&gt;</c> and persisted for hook flows.
    /// When null/omitted on Workspace sync, clears any persisted Feature base and uses the repo default.
    /// </summary>
    [JsonPropertyName("divergenceBaseBranch")]
    public string? DivergenceBaseBranch { get; set; }

    /// <summary>
    /// When true and this call cloned the repository, check out the commit recorded for it in the Workspace
    /// definition if that entry is on a tag. Existing checkouts are left where they are.
    /// </summary>
    [JsonPropertyName("applyRepositoryTagPin")]
    public bool ApplyRepositoryTagPin { get; set; }
}
