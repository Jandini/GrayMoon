using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Worker GetWorkspaceRepositories response.</summary>
public sealed class WorkerWorkspaceRepositoriesResponse
{
    [JsonPropertyName("repositoryInfos")]
    public List<WorkerRepositoryInfoDto>? RepositoryInfos { get; set; }
}

/// <summary>Repository info element.</summary>
public sealed class WorkerRepositoryInfoDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("originUrl")]
    public string? OriginUrl { get; set; }
}

/// <summary>Worker GetWorkspaceExists response.</summary>
public sealed class WorkerWorkspaceExistsResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
}

/// <summary>Worker GetWorkspaceRepositories response (repositories array only).</summary>
public sealed class WorkerRepositoriesListResponse
{
    [JsonPropertyName("repositories")]
    public List<string>? Repositories { get; set; }
}

/// <summary>Worker UpdateFileVersions response.</summary>
public sealed class WorkerUpdateFileVersionsResponse
{
    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker GetFileContents response.</summary>
public sealed class WorkerGetFileContentsResponse
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("contentBase64")]
    public string? ContentBase64 { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker ValidatePath response.</summary>
public sealed class ValidatePathWorkerResponse
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker GetHostInfo response (subset used by App).</summary>
public sealed class GetHostInfoWorkerResponse
{
    [JsonPropertyName("userProfilePath")]
    public string? UserProfilePath { get; set; }
}
