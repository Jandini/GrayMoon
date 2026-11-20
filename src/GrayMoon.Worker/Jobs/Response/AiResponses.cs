using System.Text.Json.Serialization;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Services.Ai;

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

    /// <summary>
    /// Environment variables this provider's CLI is known to read, from its adapter. The Worker page (Environment)
    /// suggests them while the CLI is installed.
    /// </summary>
    [JsonPropertyName("knownEnvironmentVariables")]
    public List<KnownEnvironmentVariableDto> KnownEnvironmentVariables { get; set; } = [];

    /// <summary>A host value the App could store: one line, within the length limit. Anything else counts as not configured.</summary>
    private static string? UsableHostValue(string? value) =>
        string.IsNullOrEmpty(value) || AgentEnvironment.ValueError(value) is not null ? null : value;

    public static AiProviderAvailabilityDto From(AiCliAvailability availability) =>
        From(availability, HostEnvironmentVariables.Read);

    /// <param name="readHostVariable">How a known variable's value on this host is read; a test passes its own.</param>
    public static AiProviderAvailabilityDto From(AiCliAvailability availability, Func<string, string?> readHostVariable) => new()
    {
        ProviderId = availability.ProviderId,
        IsInstalled = availability.IsInstalled,
        Version = availability.Version,
        HasNoTools = availability.HasNoTools,
        Problem = availability.Problem,
        KnownEnvironmentVariables = availability.KnownEnvironmentVariables
            .Select(v => new KnownEnvironmentVariableDto
            {
                Name = v.Name,
                Value = v.Value,
                Description = v.Description,
                // Only for an installed CLI: a value for a CLI that cannot run is of no use to the App.
                CurrentValue = availability.IsInstalled ? UsableHostValue(readHostVariable(v.Name)) : null,
            })
            .ToList(),
    };
}

public sealed class KnownEnvironmentVariableDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The value that switches the behavior on; filled in when the user picks the suggestion.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// What the variable is set to on this host already (<see cref="HostEnvironmentVariables.Read"/>), or null when the
    /// user has not configured it. The App lists it on the Worker page (Environment) at startup, so the grid shows what
    /// the agent runs with. Only ever sent for the variables an adapter names, never for the environment at large.
    /// </summary>
    [JsonPropertyName("currentValue")]
    public string? CurrentValue { get; set; }
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

    /// <summary>
    /// Only when <see cref="Success"/> is false: the environment variable that fixes this failure, when the run did not
    /// have it. The App stores it (Worker page, Environment) and sends the request once more.
    /// </summary>
    [JsonPropertyName("environmentFix")]
    public AgentEnvironmentFixDto? EnvironmentFix { get; set; }
}
