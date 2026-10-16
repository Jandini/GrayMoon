using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>Which AI CLIs the Worker can run, checked as the Worker's own account sees them (PATH and <c>--version</c>).</summary>
public sealed class GetAiProvidersRequest
{
    /// <summary>Ignore the short-lived cache and check again.</summary>
    [JsonPropertyName("refresh")]
    public bool Refresh { get; set; }
}

/// <summary>
/// One text generation from a complete, GrayMoon-prepared prompt. No repository, path or workspace is part of it: the
/// CLI runs in an empty folder and only ever sees this text.
/// </summary>
public sealed class GenerateAiTextRequest
{
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; set; }

    /// <summary>Null means the CLI's default model.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("userPrompt")]
    public string? UserPrompt { get; set; }

    [JsonPropertyName("jsonSchema")]
    public string? JsonSchema { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;
}
