using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class SweepPendingFeatureFoldersResponse
{
    /// <summary>Marked folders deleted in this pass.</summary>
    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    /// <summary>Marked folders still in use; they stay marked for the next pass.</summary>
    [JsonPropertyName("stillPending")]
    public int StillPending { get; set; }

    /// <summary>Marked folders a safety guard kept GrayMoon from deleting.</summary>
    [JsonPropertyName("refused")]
    public int Refused { get; set; }
}
