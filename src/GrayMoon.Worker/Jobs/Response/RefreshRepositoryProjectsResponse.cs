using System.Text.Json.Serialization;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

/// <summary>Response containing only project references (parsed .csproj). No git version or branch.</summary>
public sealed class RefreshRepositoryProjectsResponse
{
    [JsonPropertyName("projects")]
    public IReadOnlyList<CsProjFileInfo>? Projects { get; set; }
}
