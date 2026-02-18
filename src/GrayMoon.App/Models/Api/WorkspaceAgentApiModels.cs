using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Agent GetWorkspaceRepositories response.</summary>
public sealed class AgentWorkspaceRepositoriesResponse
{
    [JsonPropertyName("repositoryInfos")]
    public List<AgentRepositoryInfoDto>? RepositoryInfos { get; set; }
}

/// <summary>Repository info element.</summary>
public sealed class AgentRepositoryInfoDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("originUrl")]
    public string? OriginUrl { get; set; }
}

/// <summary>Agent GetWorkspaceRoot response.</summary>
public sealed class AgentWorkspaceRootResponse
{
    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }
}

/// <summary>Agent GetWorkspaceExists response.</summary>
public sealed class AgentWorkspaceExistsResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
}

/// <summary>Agent GetWorkspaceRepositories response (repositories array only).</summary>
public sealed class AgentRepositoriesListResponse
{
    [JsonPropertyName("repositories")]
    public List<string>? Repositories { get; set; }
}
