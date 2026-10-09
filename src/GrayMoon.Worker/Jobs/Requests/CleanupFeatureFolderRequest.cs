using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Deletes a removed Feature's leftover folder, or marks it pending deletion (<c>GRAYMOON-PENDING-DELETE.md</c>) when
/// files are still in use. Sent once at the end of Remove Feature, and before Create Feature reuses the same name.
/// </summary>
public sealed class CleanupFeatureFolderRequest
{
    /// <summary>The Workspace's Feature storage root, ending in <c>&lt;Workspace&gt;\features</c>.</summary>
    [JsonPropertyName("featureStorageRoot")]
    public string? FeatureStorageRoot { get; set; }

    /// <summary>The Feature folder, a direct child of <see cref="FeatureStorageRoot"/>.</summary>
    [JsonPropertyName("featureRootPath")]
    public string? FeatureRootPath { get; set; }

    /// <summary>Workspace name written into the marker; optional.</summary>
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Feature name written into the marker; optional.</summary>
    [JsonPropertyName("featureName")]
    public string? FeatureName { get; set; }

    /// <summary>When true, retries each delete briefly (a file closing a moment later); false tries each entry once.</summary>
    [JsonPropertyName("retry")]
    public bool Retry { get; set; }

    /// <summary>When true, only a folder carrying a matching pending-delete marker is touched (Create Feature reusing a name).</summary>
    [JsonPropertyName("onlyIfMarked")]
    public bool OnlyIfMarked { get; set; }
}
