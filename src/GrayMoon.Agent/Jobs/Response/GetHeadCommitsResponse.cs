using System.Text.Json.Serialization;
namespace GrayMoon.Agent.Jobs.Response;
public sealed class GetHeadCommitsResponse
{
    /// <summary>Map of repository name to full HEAD SHA. Missing or failed repos are omitted.</summary>
    [JsonPropertyName("commits")] public Dictionary<string, string>? Commits { get; set; }
}
