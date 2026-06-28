using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Response from GET /api/branches/get and POST /api/branches/refresh.</summary>
public sealed class BranchesResponse
{
    [JsonPropertyName("localBranches")]
    public List<string> LocalBranches { get; set; } = new();

    [JsonPropertyName("remoteBranches")]
    public List<string> RemoteBranches { get; set; } = new();

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    /// <summary>Tag names available in the repository. Empty when no tags are present.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>Tag name HEAD is currently checked out at (detached HEAD on a tag). Null when on a branch.</summary>
    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    /// <summary>Present on refresh response; when false, ErrorMessage describes the failure (e.g. fetch failed).</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Response from POST /api/branches/checkout. Agent may send PascalCase; use case-insensitive deserialization.</summary>
public sealed class CheckoutBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent response for CheckoutTag command (camelCase).</summary>
public sealed class CheckoutTagResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent CreateBranch response (camelCase).</summary>
public sealed class CreateBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    // Populated only when SkipHooks=true (inline sync path)
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

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

    [JsonPropertyName("fetchError")]
    public string? FetchError { get; set; }
}

/// <summary>Agent DeleteBranch response (camelCase).</summary>
public sealed class DeleteBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent SetUpstreamBranch response (camelCase).</summary>
public sealed class SetUpstreamBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent SyncToDefaultBranch response (camelCase). Used to parse agent response.Data.</summary>
public sealed class SyncToDefaultBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

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

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("projects")]
    public List<AgentProjectDto>? Projects { get; set; }
}

/// <summary>API response for POST /api/branches/delete (camelCase).</summary>
public sealed class DeleteBranchApiResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>API response for POST /api/branches/create and /api/branches/set-upstream (camelCase).</summary>
public sealed class CreateBranchApiResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Agent StageAndCommit response (camelCase).</summary>
public sealed class StageAndCommitResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("committed")]
    public bool Committed { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent SyncRepositoryDependencies response (camelCase).</summary>
public sealed class SyncRepositoryDependenciesResponse
{
    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }
}

/// <summary>Agent PushRepository response (camelCase).</summary>
public sealed class PushRepositoryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Response from POST /api/branches/common (camelCase).</summary>
public sealed class CommonBranchesApiResult
{
    [JsonPropertyName("commonBranchNames")]
    public List<string>? CommonBranchNames { get; set; }

    [JsonPropertyName("commonLocalBranchNames")]
    public List<string>? CommonLocalBranchNames { get; set; }

    [JsonPropertyName("commonRemoteBranchNames")]
    public List<string>? CommonRemoteBranchNames { get; set; }

    /// <summary>Display text for the default option: branch name when all repos share same default (e.g. "main"), or "multiple" when they differ.</summary>
    [JsonPropertyName("defaultDisplayText")]
    public string? DefaultDisplayText { get; set; }
}
