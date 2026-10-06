using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class AttachWorkspaceRepositoryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>True when the checked-out branch has no commit yet (the remote was empty).</summary>
    [JsonPropertyName("isUnborn")]
    public bool IsUnborn { get; set; }
}
