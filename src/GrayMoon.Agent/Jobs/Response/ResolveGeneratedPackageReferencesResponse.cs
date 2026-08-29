using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class ResolveGeneratedPackageReferencesResponse
{
    [JsonPropertyName("files")] public List<ResolveGeneratedPackageReferencesFileResult>? Files { get; set; }
}

public sealed class ResolveGeneratedPackageReferencesFileResult
{
    [JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("packages")] public List<ResolveGeneratedPackageReferencesPackageEntry> Packages { get; set; } = [];
}

/// <summary>One resolved PackageReference: the repository-name token from the version pattern and the matching PackageReference Include name.</summary>
public sealed class ResolveGeneratedPackageReferencesPackageEntry
{
    [JsonPropertyName("repoNameToken")] public string RepoNameToken { get; set; } = string.Empty;
    [JsonPropertyName("packageName")] public string PackageName { get; set; } = string.Empty;
}
