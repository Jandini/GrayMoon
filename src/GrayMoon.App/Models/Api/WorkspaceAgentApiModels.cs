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

/// <summary>Agent SearchFiles response.</summary>
public sealed class AgentSearchFilesResponse
{
    [JsonPropertyName("files")]
    public List<AgentSearchFileItemDto>? Files { get; set; }
}

public sealed class AgentSearchFileItemDto
{
    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}

/// <summary>Agent UpdateFileVersions response.</summary>
public sealed class AgentUpdateFileVersionsResponse
{
    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent GetFileContents response.</summary>
public sealed class AgentGetFileContentsResponse
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
