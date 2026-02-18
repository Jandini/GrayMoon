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
