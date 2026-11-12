using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class CheckoutBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Full sha of HEAD after a successful checkout. Null when it could not be read.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>
    /// Git refused the checkout because local changes would be overwritten. Nothing was changed: the checkout is never
    /// forced and nothing is stashed.
    /// </summary>
    [JsonPropertyName("localChangesBlockCheckout")]
    public bool LocalChangesBlockCheckout { get; set; }
}
