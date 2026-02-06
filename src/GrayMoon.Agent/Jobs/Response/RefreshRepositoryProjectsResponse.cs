using System.Text.Json.Serialization;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Jobs.Response;

/// <summary>Response containing only project references (parsed .csproj). No git version or branch.</summary>
public sealed class RefreshRepositoryProjectsResponse
{
    [JsonPropertyName("projects")]
    public IReadOnlyList<CsProjFileInfo>? Projects { get; set; }
}
