using System.Text.Json.Serialization;
namespace GrayMoon.Agent.Jobs.Response;
public sealed class GetHeadCommitsResponse
{
    /// <summary>Map of repository name to full HEAD SHA. Missing or failed repos are omitted.</summary>
    [JsonPropertyName("commits")] public Dictionary<string, string>? Commits { get; set; }

    /// <summary>
    /// Map of repository name to the checked-out branch name at the same moment as <see cref="Commits"/>.
    /// Detached HEAD / unborn repos are omitted (callers treat missing as unknown parent).
    /// </summary>
    [JsonPropertyName("branches")] public Dictionary<string, string>? Branches { get; set; }
}
