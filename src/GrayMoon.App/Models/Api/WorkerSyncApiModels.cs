using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Worker SyncRepository / GetRepositoryVersion / RefreshRepositoryVersion response shape (version, branch, hasUpstream, branches).</summary>
public sealed class WorkerVersionBranchResponse
{
    /// <summary>The command's own result, distinct from the transport envelope. Null from commands that do not report one. When false the command bailed out early and every state field below is absent rather than genuinely null.</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("gitVersionError")]
    public string? GitVersionError { get; set; }

    [JsonPropertyName("gitFetchError")]
    public string? GitFetchError { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>True when the worker resolved <see cref="HasUpstream"/> from git config. Absent from workers that predate it, which leaves the persisted flag alone.</summary>
    [JsonPropertyName("upstreamProbed")]
    public bool UpstreamProbed { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }

    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }
}

/// <summary>Worker response with exists, version, branch (GetRepositoryVersion).</summary>
public sealed class WorkerGetRepositoryVersionResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>
    /// Whether the worker actually ran a version provider. False means the workspace does not version its
    /// repositories, so an empty <see cref="Version"/> is not a mismatch. Null from a worker that predates
    /// workspace profiles, which always ran one.
    /// </summary>
    [JsonPropertyName("versionProbed")]
    public bool? VersionProbed { get; set; }
}

/// <summary>Worker response with commit counts and hasUpstream (from GetCommitCounts).</summary>
public sealed class WorkerCommitCountsResponse
{
    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }
}

/// <summary>Worker response with localBranches, remoteBranches, and defaultBranch (e.g. from SyncRepository or RefreshBranches).</summary>
public sealed class WorkerBranchesResponse
{
    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }
}

/// <summary>Project element in worker sync response (CsProjFileInfo shape).</summary>
public sealed class WorkerProjectDto
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
    public List<WorkerPackageRefDto>? PackageReferences { get; set; }
}

/// <summary>Package reference in worker project.</summary>
public sealed class WorkerPackageRefDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>Worker FetchCommits response - commit counts plus tags and current tag for HasNewerTag update.</summary>
public sealed class WorkerFetchCommitsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }
}

/// <summary>Worker sync response with projects array.</summary>
public sealed class WorkerSyncProjectsResponse
{
    [JsonPropertyName("projects")]
    public List<WorkerProjectDto>? Projects { get; set; }
}
