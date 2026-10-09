using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class CleanupFeatureFolderResponse
{
    /// <summary><c>Removed</c>, <c>PendingDeletion</c> or <c>Refused</c>.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>Files still left in the folder; 0 unless <see cref="Outcome"/> is <c>PendingDeletion</c>.</summary>
    [JsonPropertyName("remainingFileCount")]
    public int RemainingFileCount { get; set; }

    /// <summary>Why the folder was left or refused; null when it was removed.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
