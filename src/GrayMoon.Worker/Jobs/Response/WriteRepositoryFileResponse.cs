using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class WriteRepositoryFileResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>False when the content was already identical and nothing was written.</summary>
    [JsonPropertyName("written")]
    public bool Written { get; set; }
}
