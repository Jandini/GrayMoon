using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Agent SyncRepository / GetRepositoryVersion response shape (version, branch, etc.).</summary>
public sealed class AgentVersionBranchResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}

/// <summary>Agent response with exists, version, branch (GetRepositoryVersion).</summary>
public sealed class AgentGetRepositoryVersionResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }
}

/// <summary>Agent response with commit counts.</summary>
public sealed class AgentCommitCountsResponse
{
    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }
}

/// <summary>Agent response with localBranches and remoteBranches arrays.</summary>
public sealed class AgentBranchesResponse
{
    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }
}

/// <summary>Project element in agent sync response (CsProjFileInfo shape).</summary>
public sealed class AgentProjectDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("projectType")]
    public int ProjectType { get; set; }

    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; set; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; set; }

    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }

    [JsonPropertyName("packageReferences")]
    public List<AgentPackageRefDto>? PackageReferences { get; set; }
}

/// <summary>Package reference in agent project.</summary>
public sealed class AgentPackageRefDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>Agent sync response with projects array.</summary>
public sealed class AgentSyncProjectsResponse
{
    [JsonPropertyName("projects")]
    public List<AgentProjectDto>? Projects { get; set; }
}
