using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetCapabilitiesResponse
{
    [JsonPropertyName("supportedFeatures")]
    public List<string>? SupportedFeatures { get; set; }
}
