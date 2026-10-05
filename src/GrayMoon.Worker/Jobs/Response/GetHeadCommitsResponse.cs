using System.Text.Json.Serialization;
namespace GrayMoon.Worker.Jobs.Response;
public sealed class GetHeadCommitsResponse
{
    /// <summary>Map of repository name to full HEAD SHA. Missing or failed repos are omitted.</summary>
    [JsonPropertyName("commits")] public Dictionary<string, string>? Commits { get; set; }

    /// <summary>
    /// Map of repository name to the checked-out branch name at the same moment as <see cref="Commits"/>.
    /// Detached HEAD / unborn repos are omitted (callers treat missing as unknown parent).
    /// </summary>
    [JsonPropertyName("branches")] public Dictionary<string, string>? Branches { get; set; }

    /// <summary>Map of repository name to the tag checked out (detached HEAD at an exact tag). Other repos are omitted.</summary>
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }

    /// <summary>
    /// When the request sets a collision branch name: map of repository name to existing refs that collide with it.
    /// Repositories without collisions, and repositories on a tag (no branch is created there), are omitted.
    /// </summary>
    [JsonPropertyName("branchCollisions")] public Dictionary<string, List<string>>? BranchCollisions { get; set; }
}
