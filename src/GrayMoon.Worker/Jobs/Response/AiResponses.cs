using System.Text.Json.Serialization;
using GrayMoon.Common.Ai;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetAiProvidersResponse
{
    [JsonPropertyName("providers")]
    public List<AiProviderAvailabilityDto> Providers { get; set; } = [];
}

public sealed class AiProviderAvailabilityDto
{
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; set; }

    [JsonPropertyName("isInstalled")]
    public bool IsInstalled { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>False when the CLI can only be limited to a read-only mode, not to no tools at all.</summary>
    [JsonPropertyName("hasNoTools")]
    public bool HasNoTools { get; set; }

    [JsonPropertyName("problem")]
    public string? Problem { get; set; }
}

public sealed class GenerateAiTextResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>The provider's JSON answer (not yet validated against the expected shape; the App does that).</summary>
    [JsonPropertyName("json")]
    public string? Json { get; set; }

    [JsonPropertyName("errorKind")]
    public AiGenerationErrorKind ErrorKind { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    /// <summary>
    /// Optional warning candidate when <see cref="Success"/> is true (for example Cursor exited nonzero after a verified
    /// result). Older App binaries ignore this field; the App must still validate <see cref="Json"/> before showing it.
    /// </summary>
    [JsonPropertyName("warning")]
    public AiGenerationWarningDto? Warning { get; set; }
}
