using System.Text.Json.Serialization;
using GrayMoon.Common.Ai;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class CollectGitEvidenceResponse
{
    /// <summary>One entry per requested repository, in request order; a repository that failed has <c>success: false</c>.</summary>
    [JsonPropertyName("repositories")]
    public List<RepositoryGitEvidence> Repositories { get; set; } = [];

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }
}
