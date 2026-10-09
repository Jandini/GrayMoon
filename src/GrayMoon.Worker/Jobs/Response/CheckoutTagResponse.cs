using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class CheckoutTagResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    /// <summary>Full commit hash HEAD points at after the checkout.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
