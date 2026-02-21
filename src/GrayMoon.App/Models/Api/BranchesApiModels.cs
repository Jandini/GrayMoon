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

/// <summary>Agent CreateBranch response (camelCase).</summary>
public sealed class CreateBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Agent StageAndCommit response (camelCase).</summary>
public sealed class StageAndCommitResponse
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

    /// <summary>Display text for the default option: branch name when all repos share same default (e.g. "main"), or "multiple" when they differ.</summary>
    [JsonPropertyName("defaultDisplayText")]
    public string? DefaultDisplayText { get; set; }
}
