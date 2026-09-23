using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Response;

public sealed class GetFileContentsResponse
{
    [JsonPropertyName("content")] public string? Content { get; set; }

    /// <summary>Base64 file bytes when the request asked for <c>asBase64</c>.</summary>
    [JsonPropertyName("contentBase64")] public string? ContentBase64 { get; set; }

    /// <summary>MIME type guess from extension when returning Base64 (e.g. image/png).</summary>
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }

    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}
